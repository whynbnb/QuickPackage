#ifndef QUICKPACKAGE_PACKAGEMANAGER_H
#define QUICKPACKAGE_PACKAGEMANAGER_H

#include "PackageModel.h"

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
    // never left in a partially upgraded state.
    void update(const QVector<PackageModel::Package> &packages);
    void cancel();

    // Cancels pending work and blocks until all pool tasks finished, so no
    // worker can touch a destroyed PackageManager.
    void shutdown();

    QProcessEnvironment operationEnvironment() const;

    // Enables/disables a backend; disabled backends are skipped by the
    // refresh and update checks.
    void setBackendEnabled(PackageModel::Backend backend, bool enabled);

signals:
    void started(const QString &message);
    void progress(const QString &message);
    void finished(bool ok, const QString &message);
    void packagesLoaded(const QVector<PackageModel::Package> &packages);
    void updatesReady(const QSet<QString> &pacmanUpdates,
                      const QSet<QString> &flatpakUpdates,
                      const QSet<QString> &cargoUpdates);

public slots:
    // Accepts one or both proxy URLs, e.g. "http://127.0.0.1:7890" or
    // "socks5://127.0.0.1:1080"; empty strings clear the proxy.
    void setProxy(const QString &httpUrl, const QString &socksUrl);

private:
    enum class Op { Install, Remove, Update };

    QVector<PackageModel::Package> queryPacman();
    QVector<PackageModel::Package> queryFlatpak();
    QVector<PackageModel::Package> queryCargo();
    QSet<QString> queryPacmanUpdates();
    QSet<QString> flatpakUpdateNames();
    QSet<QString> cargoUpdateNames();

    void runOperation(const QString &label, const QVector<PackageModel::Package> &packages,
                      Op op);

    QProcessEnvironment m_proxyEnv;
    QThreadPool *m_pool = nullptr;
    std::atomic<bool> m_cancelled{false};
    bool m_pacmanEnabled = true;
    bool m_flatpakEnabled = true;
    bool m_cargoEnabled = true;
};

#endif
