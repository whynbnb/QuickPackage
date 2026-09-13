#include "PackageManager.h"

#include <KLocalizedString>
#include <QDir>
#include <QFile>
#include <QMutex>
#include <QProcess>
#include <QRegularExpression>
#include <QTextStream>
#include <QThreadPool>

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

QVector<Package> PackageManager::queryPacman()
{
    QVector<Package> result;
    QProcess proc;
    proc.setProcessEnvironment(operationEnvironment());
    proc.start(QStringLiteral("pacman"), {QStringLiteral("-Qn")});
    if (!proc.waitForFinished(30000))
        return result;
    const QStringList lines = QString::fromUtf8(proc.readAllStandardOutput()).split(u'\n');
    for (const QString &line : lines) {
        const int sp = line.indexOf(u' ');
        if (sp <= 0)
            continue;
        Package pkg;
        pkg.name = line.left(sp);
        pkg.packageId = pkg.name;
        pkg.version = line.mid(sp + 1).trimmed();
        pkg.backend = Backend::Pacman;
        pkg.installed = true;
        pkg.origin = QStringLiteral("core/extra/multilib");
        result.append(pkg);
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
        // Format: "name version (source)" = ["binary"] lines inside a [v1] table,
        // e.g. "bat 0.26.1 (registry+https://github.com/rust-lang/crates.io-index)" = ["bat"]
        static const QRegularExpression re(
            QStringLiteral("^\\s*\"([a-zA-Z0-9_.+-]+) ([^\\s\"]+) \\([^)]*\\)\"\\s*="));
        QTextStream stream(&listFile);
        while (!stream.atEnd()) {
            const QString line = stream.readLine();
            const auto match = re.match(line);
            if (match.hasMatch()) {
                Package pkg;
                pkg.name = match.captured(1);
                pkg.packageId = pkg.name;
                pkg.version = match.captured(2);
                pkg.backend = Backend::Cargo;
                pkg.installed = true;
                pkg.origin = QStringLiteral("crates.io");
                result.append(pkg);
            }
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
    // Lines look like: "bat v0.24.0 -> v0.25.0" or "bat v0.24.0 (Up to date)"
    static const QRegularExpression re(
        QStringLiteral("^([a-zA-Z0-9_.-]+)\\s+v[0-9][^\\s]*"));
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

    auto results = std::make_shared<QVector<Package>>();
    auto mutex = std::make_shared<QMutex>();
    auto remaining = std::make_shared<std::atomic<int>>(3);
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
            Q_EMIT finished(true, i18n("Loaded %1 packages").arg(all.size()));
        }, Qt::QueuedConnection);
    };

    m_pool->start([this, merge] { merge(queryPacman()); });
    m_pool->start([this, merge] { merge(queryFlatpak()); });
    m_pool->start([this, merge] { merge(queryCargo()); });
}

void PackageManager::refreshUpdates()
{
    Q_EMIT started(i18n("Checking for updates…"));

    auto pacmanRes = std::make_shared<QSet<QString>>();
    auto flatpakRes = std::make_shared<QSet<QString>>();
    auto cargoRes = std::make_shared<QSet<QString>>();
    auto mutex = std::make_shared<QMutex>();
    auto remaining = std::make_shared<std::atomic<int>>(3);
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

    m_pool->start([this, merge] { merge(0, queryPacmanUpdates()); });
    m_pool->start([this, merge] { merge(1, flatpakUpdateNames()); });
    m_pool->start([this, merge] { merge(2, cargoUpdateNames()); });
}

void PackageManager::runOperation(const QString &label,
                                  const QVector<Package> &packages, bool installOp)
{
    Q_EMIT started(label);
    m_pool->start([this, label, packages, installOp] {
        QProcessEnvironment env = operationEnvironment();
        bool ok = true;
        QString lastError;

        for (const Package &pkg : packages) {
            if (m_cancelled) {
                ok = false;
                lastError = i18n("Cancelled");
                break;
            }
            // The command line needs the concrete package id (Flatpak app id),
            // not the display name.
            const QString id = pkg.packageId.isEmpty() ? pkg.name : pkg.packageId;
            QStringList args;
            const QString pkgLabel = pkg.name;
            if (pkg.backend == Backend::Pacman) {
                if (installOp)
                    args << QStringLiteral("sudo") << QStringLiteral("pacman")
                         << QStringLiteral("-S") << QStringLiteral("--noconfirm") << id;
                else
                    args << QStringLiteral("sudo") << QStringLiteral("pacman")
                         << QStringLiteral("-Rns") << QStringLiteral("--noconfirm") << id;
            } else if (pkg.backend == Backend::Flatpak) {
                if (installOp)
                    args << QStringLiteral("flatpak") << QStringLiteral("install")
                         << QStringLiteral("-y") << id;
                else
                    args << QStringLiteral("flatpak") << QStringLiteral("uninstall")
                         << QStringLiteral("-y") << id;
            } else {
                if (installOp)
                    args << QStringLiteral("cargo") << QStringLiteral("install") << id;
                else
                    args << QStringLiteral("cargo") << QStringLiteral("uninstall") << id;
            }

            Q_EMIT progress(installOp ? i18n("Installing %1…").arg(pkgLabel)
                                     : i18n("Removing %1…").arg(pkgLabel));
            QString output;
            if (!runCommand(args, env, &output, 1800000)) {
                ok = false;
                lastError = i18n("%1: %2").arg(pkgLabel, output.right(400));
                Q_EMIT progress(i18n("Failed: %1").arg(pkgLabel));
                continue;
            }
            Q_EMIT progress(i18n("Done: %1").arg(pkgLabel));
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
    runOperation(i18n("Installing selected packages…"), packages, true);
}

void PackageManager::remove(const QVector<Package> &packages)
{
    runOperation(i18n("Removing selected packages…"), packages, false);
}

void PackageManager::cancel()
{
    m_cancelled = true;
}
