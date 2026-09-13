#ifndef QUICKPACKAGE_PACKAGEMANAGER_H
#define QUICKPACKAGE_PACKAGEMANAGER_H

#include "PackageModel.h"

#include <QHash>
#include <QProcessEnvironment>
#include <QObject>
#include <QString>
#include <QVector>

#include <atomic>

class QThreadPool;

class PackageManager : public QObject
{
    Q_OBJECT

public:
    explicit PackageManager(QObject *parent = nullptr);
    ~PackageManager() override;

    void refreshCache();
    void refreshUpdates();
    void install(const QVector<PackageModel::Package> &packages);
    void remove(const QVector<PackageModel::Package> &packages);
    // Updates packages; pacman runs one full "pacman -Syu" so the system is
    // never left in a partially upgraded state. AUR runs one helper pass,
    // mise/rustup one tool upgrade pass.
    void update(const QVector<PackageModel::Package> &packages);
    void cancel();

    // Cancels pending work and blocks until all pool tasks finished, so no
    // worker can touch a destroyed PackageManager.
    void shutdown();

    QProcessEnvironment operationEnvironment() const;

    // Enables/disables a backend; disabled backends are skipped by the
    // refresh and update checks. Developer tool backends are off by default.
    void setBackendEnabled(PackageModel::Backend backend, bool enabled);

signals:
    void started(const QString &message);
    void progress(const QString &message);
    void finished(bool ok, const QString &message);
    void packagesLoaded(const QVector<PackageModel::Package> &packages);
    // Backend id (PackageModel::Backend) -> names with pending updates; the
    // AUR and pacman queries both report under Backend::Pacman.
    void updatesReady(const QHash<int, QSet<QString>> &updates);

public slots:
    // Accepts one or both proxy URLs, e.g. "http://127.0.0.1:7890" or
    // "socks5://127.0.0.1:1080"; empty strings clear the proxy.
    void setProxy(const QString &httpUrl, const QString &socksUrl);

private:
    enum class Op { Install, Remove, Update };

    QVector<PackageModel::Package> queryPacman();
    QVector<PackageModel::Package> queryFlatpak();
    QVector<PackageModel::Package> queryCargo();
    QVector<PackageModel::Package> queryMise();
    QVector<PackageModel::Package> queryRustup();
    QVector<PackageModel::Package> queryFvm();
    QSet<QString> queryPacmanUpdates();
    QSet<QString> aurUpdateNames();
    QSet<QString> miseUpdateNames();
    QSet<QString> rustupUpdateNames();
    QSet<QString> flatpakUpdateNames();
    QSet<QString> cargoUpdateNames();

    // AUR helper used for foreign (AUR) packages: paru if available,
    // otherwise yay; empty when neither is installed.
    QString aurHelper() const;

    void runOperation(const QString &label, const QVector<PackageModel::Package> &packages,
                      Op op);

    QProcessEnvironment m_proxyEnv;
    QThreadPool *m_pool = nullptr;
    std::atomic<bool> m_cancelled{false};
    bool m_pacmanEnabled = true;
    bool m_flatpakEnabled = true;
    bool m_cargoEnabled = true;
    // Developer tool backends are optional and disabled by default.
    bool m_miseEnabled = false;
    bool m_rustupEnabled = false;
    bool m_fvmEnabled = false;
};

#endif
