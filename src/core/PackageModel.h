#ifndef QUICKPACKAGE_PACKAGEMODEL_H
#define QUICKPACKAGE_PACKAGEMODEL_H

#include <QAbstractListModel>
#include <QSet>
#include <QString>
#include <QVariant>
#include <QVector>

class PackageModel : public QAbstractListModel
{
    Q_OBJECT

public:
    // System backends (Pacman/Flatpak/Cargo) and optional developer tool
    // managers (Mise/Rustup/Fvm), which are disabled by default.
    enum class Backend { Pacman = 0, Flatpak, Cargo, Mise, Rustup, Fvm };
    Q_ENUM(Backend)

    enum class Status { Idle = 0, Installed, Updating, Removing, Failed, NeedsUpdate };
    Q_ENUM(Status)

    enum Roles {
        NameRole = Qt::UserRole + 1,
        VersionRole,
        NewVersionRole,
        DescriptionRole,
        BackendRole,
        StatusRole,
        IsInstalledRole,
        IsSelectedRole,
        SizeRole,
        OriginRole,
        PackageIdRole,
        IsForeignRole
    };

    struct Package {
        QString name;
        QString packageId;
        QString version;
        QString newVersion;
        QString description;
        QString origin;
        QString size;
        QString installDate;
        QString installReason;
        QString packager;
        QString url;
        QString licenses;
        QString depends;
        QString binaries;
        Backend backend = Backend::Pacman;
        Status status = Status::Idle;
        bool installed = false;
        bool selected = false;
        // Foreign (AUR/local) package: installed by pacman but absent from the
        // sync repositories, so it needs an AUR helper (paru/yay) to update.
        bool foreign = false;
    };

    explicit PackageModel(QObject *parent = nullptr);

    int rowCount(const QModelIndex &parent = {}) const override;
    QVariant data(const QModelIndex &index, int role = Qt::DisplayRole) const override;
    QHash<int, QByteArray> roleNames() const override;

    void setPackages(const QVector<Package> &packages);
    Q_INVOKABLE Package packageAt(int row) const;
    void updatePackageStatus(const QString &nameOrId, Backend backend, Status status);
    void setPackageInstalled(const QString &nameOrId, Backend backend, bool installed,
                             const QString &version = {});
    void setSelected(const QString &nameOrId, Backend backend, bool selected);
    void clearSelection();
    QVector<Package> selectedPackages() const;
    int selectedCount() const;
    int updateCount() const;
    int updateCount(Backend backend) const;

    static QString backendName(Backend backend);
    static QString statusText(Status status);

private:
    QVector<Package> m_packages;
    QSet<QPair<QString, int>> m_keys;
};

#endif
