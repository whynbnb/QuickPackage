#include "PackageManager.h"

#include <KLocalizedString>
#include <QDir>
#include <QFile>
#include <QMutex>
#include <QProcess>
#include <QRegularExpression>
#include <QTextStream>
#include <QThreadPool>

#include <algorithm>
#include <memory>
#include <utility>

using Package = PackageModel::Package;
using Backend = PackageModel::Backend;

namespace {

bool runCommand(const QStringList &args, const QProcessEnvironment &env, QString *output,
                int timeoutMs = 60000)
{
    QProcess proc;
    proc.setProcessEnvironment(env);
    proc.start(args.first(), args.mid(1));
    bool ok = proc.waitForStarted(5000);
    if (ok)
        ok = proc.waitForFinished(timeoutMs);
    if (output)
        *output = QString::fromUtf8(proc.readAllStandardOutput() + proc.readAllStandardError());
    return ok && proc.exitStatus() == QProcess::NormalExit && proc.exitCode() == 0;
}

} // namespace

QProcessEnvironment PackageManager::operationEnvironment() const
{
    return m_proxyEnv;
}

PackageManager::PackageManager(QObject *parent)
    : QObject(parent)
    , m_pool(new QThreadPool(this))
{
    m_pool->setMaxThreadCount(4);
}

PackageManager::~PackageManager()
{
    shutdown();
}

void PackageManager::shutdown()
{
    m_cancelled = true;
    if (m_pool) {
        m_pool->clear();
        m_pool->waitForDone();
    }
}

void PackageManager::setProxy(const QString &httpUrl, const QString &socksUrl)
{
    m_proxyEnv = QProcessEnvironment::systemEnvironment();
    const QString noProxy = QStringLiteral("localhost,127.0.0.1,::1");
    m_proxyEnv.insert(QStringLiteral("NO_PROXY"), noProxy);
    m_proxyEnv.insert(QStringLiteral("no_proxy"), noProxy);

    const QString url = socksUrl.isEmpty() ? httpUrl : socksUrl;
    if (url.isEmpty())
        return;

    for (const QString &key :
         {QStringLiteral("ALL_PROXY"), QStringLiteral("all_proxy")})
        m_proxyEnv.insert(key, url);

    if (!httpUrl.isEmpty()) {
        for (const QString &key :
             {QStringLiteral("http_proxy"), QStringLiteral("https_proxy"),
              QStringLiteral("HTTP_PROXY"), QStringLiteral("HTTPS_PROXY")})
            m_proxyEnv.insert(key, httpUrl);
    } else {
        // curl/libcurl-based tools (pacman, cargo, flatpak) also honour the
        // socks5:// scheme in http_proxy/https_proxy.
        m_proxyEnv.insert(QStringLiteral("http_proxy"), url);
        m_proxyEnv.insert(QStringLiteral("https_proxy"), url);
    }
}

void PackageManager::setBackendEnabled(PackageModel::Backend backend, bool enabled)
{
    switch (backend) {
    case Backend::Pacman:
        m_pacmanEnabled = enabled;
        break;
    case Backend::Flatpak:
        m_flatpakEnabled = enabled;
        break;
    case Backend::Cargo:
        m_cargoEnabled = enabled;
        break;
    }
}

QVector<Package> PackageManager::queryPacman()
{
    QVector<Package> result;
    QProcess proc;
    // LANG/LC_ALL=C keep the "key : value" layout stable for parsing; with the
    // user's locale pacman translates the keys and dates.
    QProcessEnvironment env = operationEnvironment();
    env.insert(QStringLiteral("LANG"), QStringLiteral("C"));
    env.insert(QStringLiteral("LC_ALL"), QStringLiteral("C"));
    proc.setProcessEnvironment(env);
    proc.start(QStringLiteral("pacman"), {QStringLiteral("-Qi")});
    if (!proc.waitForFinished(60000))
        return result;
    const QStringList lines = QString::fromUtf8(proc.readAllStandardOutput()).split(u'\n');
    Package pkg;
    QString *current = nullptr;
    const auto flush = [&] {
        if (!pkg.name.isEmpty()) {
            pkg.packageId = pkg.name;
            pkg.backend = Backend::Pacman;
            pkg.installed = true;
            result.append(pkg);
        }
        pkg = Package{};
    };
    for (const QString &line : lines) {
        if (line.trimmed().isEmpty()) {
            flush();
            current = nullptr;
            continue;
        }
        const int sep = line.indexOf(QStringLiteral(" : "));
        if (sep < 0) {
            // Continuation of the previous (wrapped) value.
            if (current)
                *current += QStringLiteral(" ") + line.trimmed();
            continue;
        }
        const QString key = line.left(sep).trimmed();
        const QString value = line.mid(sep + 3).trimmed();
        if (key == QLatin1String("Name")) {
            pkg.name = value;
            current = &pkg.name;
        } else if (key == QLatin1String("Version")) {
            pkg.version = value;
            current = &pkg.version;
        } else if (key == QLatin1String("Description")) {
            pkg.description = value;
            current = &pkg.description;
        } else if (key == QLatin1String("Repository")) {
            pkg.origin = value;
            current = &pkg.origin;
        } else if (key == QLatin1String("Installed Size")) {
            pkg.size = value;
            current = &pkg.size;
        } else if (key == QLatin1String("Install Date")) {
            pkg.installDate = value;
            current = &pkg.installDate;
        } else if (key == QLatin1String("Install Reason")) {
            pkg.installReason = value;
            current = &pkg.installReason;
        } else if (key == QLatin1String("Packager")) {
            pkg.packager = value;
            current = &pkg.packager;
        } else if (key == QLatin1String("URL")) {
            pkg.url = value;
            current = &pkg.url;
        } else if (key == QLatin1String("Licenses")) {
            pkg.licenses = value;
            current = &pkg.licenses;
        } else if (key == QLatin1String("Depends On")) {
            pkg.depends = value;
            current = &pkg.depends;
        } else {
            current = nullptr;
        }
    }
    flush();

    // pacman -Qi carries no repository field on this system; map installed
    // packages to their repo via "pacman -Sl" ("core bash 5.3.15-1 [installed]").
    // Anything absent from the sync databases is a foreign (AUR/local) package.
    QProcess listProc;
    listProc.setProcessEnvironment(env);
    listProc.start(QStringLiteral("pacman"), {QStringLiteral("-Sl")});
    if (listProc.waitForFinished(60000)) {
        QHash<QString, QString> repoByName;
        static const QRegularExpression repoRe(
            QStringLiteral("^(\\S+) (\\S+) \\S+ \\[installed\\]$"));
        for (const QString &line :
             QString::fromUtf8(listProc.readAllStandardOutput()).split(u'\n')) {
            const auto match = repoRe.match(line.trimmed());
            if (match.hasMatch() && !repoByName.contains(match.captured(2)))
                repoByName.insert(match.captured(2), match.captured(1));
        }
        for (Package &item : result) {
            const auto it = repoByName.constFind(item.name);
            if (it != repoByName.constEnd())
                item.origin = it.value();
            else if (item.origin.isEmpty() || item.origin == QLatin1String("None"))
                item.origin = i18n("AUR / local");
        }
    }
    return result;
}

QVector<Package> PackageManager::queryFlatpak()
{
    QVector<Package> result;
    QString output;
    if (!runCommand({QStringLiteral("flatpak"), QStringLiteral("list"), QStringLiteral("--app"),
                     QStringLiteral("--columns=application,name,version,origin,description")},
                    operationEnvironment(), &output))
        return result;
    for (const QString &line : output.split(u'\n')) {
        const QStringList cols = line.split(u'\t');
        if (cols.isEmpty() || cols.first().isEmpty())
            continue;
        Package pkg;
        pkg.packageId = cols.value(0);
        pkg.name = cols.value(1);
        if (pkg.name.isEmpty())
            pkg.name = pkg.packageId;
        pkg.version = cols.value(2);
        pkg.origin = cols.value(3);
        pkg.description = cols.value(4);
        pkg.backend = Backend::Flatpak;
        pkg.installed = true;
        result.append(pkg);
    }
    return result;
}

QVector<Package> PackageManager::queryCargo()
{
    QVector<Package> result;
    const QString cargoHome =
        qEnvironmentVariable("CARGO_HOME", QDir::homePath() + QStringLiteral("/.cargo"));
    QFile listFile(cargoHome + QStringLiteral("/.crates.toml"));
    if (listFile.exists() && listFile.open(QIODevice::ReadOnly)) {
        // Format: "name version (source)" = ["bin1", "bin2"] lines inside a
        // [v1] table, e.g.
        // "bat 0.26.1 (registry+https://github.com/rust-lang/crates.io-index)" = ["bat"]
        static const QRegularExpression re(
            QStringLiteral("^\\s*\"([a-zA-Z0-9_.+-]+) ([^\\s\"]+) \\(([^)]*)\\)\"\\s*="
                           "\\s*\\[([^\\]]*)\\]"));
        QTextStream stream(&listFile);
        while (!stream.atEnd()) {
            const QString line = stream.readLine();
            const auto match = re.match(line);
            if (!match.hasMatch())
                continue;
            Package pkg;
            pkg.name = match.captured(1);
            pkg.packageId = pkg.name;
            pkg.version = match.captured(2);
            pkg.backend = Backend::Cargo;
            pkg.installed = true;
            const QString source = match.captured(3);
            if (source.startsWith(QLatin1String("registry+")))
                pkg.origin = QStringLiteral("crates.io");
            else if (source.startsWith(QLatin1String("git+")))
                pkg.origin = QStringLiteral("git");
            else if (source.startsWith(QLatin1String("path+")))
                pkg.origin = i18n("local path");
            else
                pkg.origin = source;
            QStringList binaries;
            for (const QString &bin : match.captured(4).split(u','))
                binaries.append(bin.trimmed().remove(u'"'));
            pkg.binaries = binaries.join(QStringLiteral(", "));
            result.append(pkg);
        }
    }
    return result;
}

QSet<QString> PackageManager::queryPacmanUpdates()
{
    QSet<QString> updates;
    QProcess proc;
    proc.setProcessEnvironment(operationEnvironment());
    proc.start(QStringLiteral("checkupdates"), {});
    if (!proc.waitForFinished(60000))
        return updates;
    for (const QString &line : QString::fromUtf8(proc.readAllStandardOutput()).split(u'\n')) {
        const int sp = line.indexOf(u' ');
        if (sp > 0)
            updates.insert(line.left(sp));
    }
    return updates;
}

QSet<QString> PackageManager::flatpakUpdateNames()
{
    QSet<QString> updates;
    QString output;
    if (!runCommand({QStringLiteral("flatpak"), QStringLiteral("update"), QStringLiteral("--app"),
                     QStringLiteral("--columns=application")}, operationEnvironment(), &output))
        return updates;
    for (const QString &line : output.split(u'\n')) {
        const QString name = line.trimmed();
        if (!name.isEmpty())
            updates.insert(name);
    }
    return updates;
}

QSet<QString> PackageManager::cargoUpdateNames()
{
    QSet<QString> updates;
    QString output;
    if (!runCommand({QStringLiteral("cargo"), QStringLiteral("install-update"), QStringLiteral("--list")},
                    operationEnvironment(), &output, 120000))
        return updates;
    // Rows: "name vInstalled vLatest Yes|No" — only rows with "Yes" need an
    // update; the header and polling lines do not match.
    static const QRegularExpression re(
        QStringLiteral("^([a-zA-Z0-9_.-]+)\\s+v\\S+\\s+v\\S+\\s+Yes\\b"));
    for (const QString &line : output.split(u'\n')) {
        const auto match = re.match(line.trimmed());
        if (match.hasMatch())
            updates.insert(match.captured(1));
    }
    return updates;
}

void PackageManager::refreshCache()
{
    m_cancelled = false;
    Q_EMIT started(i18n("Loading package lists…"));

    const int count = (m_pacmanEnabled ? 1 : 0) + (m_flatpakEnabled ? 1 : 0)
        + (m_cargoEnabled ? 1 : 0);
    if (count == 0) {
        QMetaObject::invokeMethod(this, [this] {
            Q_EMIT packagesLoaded({});
            Q_EMIT finished(true, i18n("No package backends enabled"));
        }, Qt::QueuedConnection);
        return;
    }

    auto results = std::make_shared<QVector<Package>>();
    auto mutex = std::make_shared<QMutex>();
    auto remaining = std::make_shared<std::atomic<int>>(count);
    auto merge = [this, results, mutex, remaining](QVector<Package> part) {
        {
            QMutexLocker locker(mutex.get());
            results->append(part);
        }
        if (remaining->fetch_sub(1) != 1)
            return;
        QVector<Package> all;
        {
            QMutexLocker locker(mutex.get());
            all = *results;
        }
        QMetaObject::invokeMethod(this, [this, all] {
            Q_EMIT packagesLoaded(all);
            Q_EMIT finished(true, i18n("Loaded %1 packages", all.size()));
        }, Qt::QueuedConnection);
    };

    if (m_pacmanEnabled)
        m_pool->start([this, merge] { merge(queryPacman()); });
    if (m_flatpakEnabled)
        m_pool->start([this, merge] { merge(queryFlatpak()); });
    if (m_cargoEnabled)
        m_pool->start([this, merge] { merge(queryCargo()); });
}

void PackageManager::refreshUpdates()
{
    Q_EMIT started(i18n("Checking for updates…"));

    const int count = (m_pacmanEnabled ? 1 : 0) + (m_flatpakEnabled ? 1 : 0)
        + (m_cargoEnabled ? 1 : 0);
    if (count == 0) {
        QMetaObject::invokeMethod(this, [this] {
            Q_EMIT updatesReady({}, {}, {});
            Q_EMIT finished(true, i18n("No package backends enabled"));
        }, Qt::QueuedConnection);
        return;
    }

    auto pacmanRes = std::make_shared<QSet<QString>>();
    auto flatpakRes = std::make_shared<QSet<QString>>();
    auto cargoRes = std::make_shared<QSet<QString>>();
    auto mutex = std::make_shared<QMutex>();
    auto remaining = std::make_shared<std::atomic<int>>(count);
    auto merge = [this, pacmanRes, flatpakRes, cargoRes, mutex,
                  remaining](int which, QSet<QString> part) {
        {
            QMutexLocker locker(mutex.get());
            switch (which) {
            case 0:
                *pacmanRes = part;
                break;
            case 1:
                *flatpakRes = part;
                break;
            default:
                *cargoRes = part;
                break;
            }
        }
        if (remaining->fetch_sub(1) != 1)
            return;
        QSet<QString> pacman, flatpak, cargo;
        {
            QMutexLocker locker(mutex.get());
            pacman = *pacmanRes;
            flatpak = *flatpakRes;
            cargo = *cargoRes;
        }
        QMetaObject::invokeMethod(this, [this, pacman, flatpak, cargo] {
            Q_EMIT updatesReady(pacman, flatpak, cargo);
            Q_EMIT finished(true, i18n("Update check finished"));
        }, Qt::QueuedConnection);
    };

    if (m_pacmanEnabled)
        m_pool->start([this, merge] { merge(0, queryPacmanUpdates()); });
    if (m_flatpakEnabled)
        m_pool->start([this, merge] { merge(1, flatpakUpdateNames()); });
    if (m_cargoEnabled)
        m_pool->start([this, merge] { merge(2, cargoUpdateNames()); });
}

void PackageManager::runOperation(const QString &label,
                                  const QVector<Package> &packages, Op op)
{
    Q_EMIT started(label);
    m_pool->start([this, label, packages, op] {
        QProcessEnvironment env = operationEnvironment();
        bool ok = true;
        QString lastError;

        // Pacman must never be partially updated: one full "-Syu" upgrades the
        // whole system (and the requested packages with it). Flatpak/cargo
        // targets below update only their own packages.
        if (op == Op::Update) {
            const bool hasPacman = std::any_of(packages.cbegin(), packages.cend(),
                                               [](const Package &p) {
                                                   return p.backend == Backend::Pacman;
                                               });
            if (hasPacman) {
                if (m_cancelled) {
                    QMetaObject::invokeMethod(this, [this] {
                        Q_EMIT finished(false, i18n("Cancelled"));
                    }, Qt::QueuedConnection);
                    return;
                }
                Q_EMIT progress(i18n("Upgrading system (pacman -Syu)…"));
                QString output;
                if (!runCommand({QStringLiteral("sudo"), QStringLiteral("pacman"),
                                 QStringLiteral("-Syu"), QStringLiteral("--noconfirm")},
                                env, &output, 3600000)) {
                    ok = false;
                    lastError = i18n("pacman -Syu: %1", output.right(400));
                    Q_EMIT progress(i18n("Failed: pacman -Syu"));
                }
            }
        }

        for (const Package &pkg : packages) {
            if (m_cancelled) {
                ok = false;
                lastError = i18n("Cancelled");
                break;
            }
            // The full system upgrade above already covered pacman updates.
            if (op == Op::Update && pkg.backend == Backend::Pacman)
                continue;
            // The command line needs the concrete package id (Flatpak app id),
            // not the display name.
            const QString id = pkg.packageId.isEmpty() ? pkg.name : pkg.packageId;
            QStringList args;
            const QString pkgLabel = pkg.name;
            if (pkg.backend == Backend::Pacman) {
                if (op == Op::Install)
                    args << QStringLiteral("sudo") << QStringLiteral("pacman")
                         << QStringLiteral("-S") << QStringLiteral("--noconfirm") << id;
                else
                    args << QStringLiteral("sudo") << QStringLiteral("pacman")
                         << QStringLiteral("-Rns") << QStringLiteral("--noconfirm") << id;
            } else if (pkg.backend == Backend::Flatpak) {
                if (op == Op::Remove)
                    args << QStringLiteral("flatpak") << QStringLiteral("uninstall")
                         << QStringLiteral("-y") << id;
                else
                    args << QStringLiteral("flatpak") << QStringLiteral("update")
                         << QStringLiteral("-y") << id;
            } else {
                if (op == Op::Remove)
                    args << QStringLiteral("cargo") << QStringLiteral("uninstall") << id;
                else
                    args << QStringLiteral("cargo") << QStringLiteral("install") << id;
            }

            Q_EMIT progress(op == Op::Remove ? i18n("Removing %1…", pkgLabel)
                                             : i18n("Updating %1…", pkgLabel));
            QString output;
            if (!runCommand(args, env, &output, 1800000)) {
                ok = false;
                lastError = i18n("%1: %2", pkgLabel, output.right(400));
                Q_EMIT progress(i18n("Failed: %1", pkgLabel));
                continue;
            }
            Q_EMIT progress(i18n("Done: %1", pkgLabel));
        }
        const bool wasOk = ok;
        const QString err = lastError;
        QMetaObject::invokeMethod(this, [this, wasOk, err] {
            Q_EMIT finished(wasOk, err);
        }, Qt::QueuedConnection);
    });
}

void PackageManager::install(const QVector<Package> &packages)
{
    runOperation(i18n("Installing selected packages…"), packages, Op::Install);
}

void PackageManager::remove(const QVector<Package> &packages)
{
    runOperation(i18n("Removing selected packages…"), packages, Op::Remove);
}

void PackageManager::update(const QVector<Package> &packages)
{
    runOperation(i18n("Updating packages…"), packages, Op::Update);
}

void PackageManager::cancel()
{
    m_cancelled = true;
}
