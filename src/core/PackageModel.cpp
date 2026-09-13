#include "PackageModel.h"

#include <KLocalizedString>

PackageModel::PackageModel(QObject *parent)
    : QAbstractListModel(parent)
{
}

int PackageModel::rowCount(const QModelIndex &parent) const
{
    return parent.isValid() ? 0 : m_packages.size();
}

QVariant PackageModel::data(const QModelIndex &index, int role) const
{
    if (!index.isValid() || index.row() >= m_packages.size())
        return {};

    const Package &pkg = m_packages.at(index.row());
    switch (role) {
    case NameRole:
        return pkg.name;
    case VersionRole:
        return pkg.version;
    case NewVersionRole:
        return pkg.newVersion;
    case DescriptionRole:
        return pkg.description;
    case BackendRole:
        return static_cast<int>(pkg.backend);
    case StatusRole:
        return static_cast<int>(pkg.status);
    case IsInstalledRole:
        return pkg.installed;
    case IsSelectedRole:
        return pkg.selected;
    case SizeRole:
        return pkg.size;
    case OriginRole:
        return pkg.origin;
    case PackageIdRole:
        return pkg.packageId;
    case IsForeignRole:
        return pkg.foreign;
    default:
        return {};
    }
}

QHash<int, QByteArray> PackageModel::roleNames() const
{
    return {
        {NameRole, "name"},
        {VersionRole, "version"},
        {NewVersionRole, "newVersion"},
        {DescriptionRole, "description"},
        {BackendRole, "backend"},
        {StatusRole, "status"},
        {IsInstalledRole, "installed"},
        {IsSelectedRole, "selected"},
        {SizeRole, "size"},
        {OriginRole, "origin"},
        {PackageIdRole, "packageId"},
        {IsForeignRole, "foreign"}};
}

PackageModel::Package PackageModel::packageAt(int row) const
{
    if (row < 0 || row >= m_packages.size())
        return {};
    return m_packages.at(row);
}

void PackageModel::setPackages(const QVector<Package> &packages)
{
    beginResetModel();
    m_packages = packages;
    m_keys.clear();
    for (const Package &pkg : std::as_const(m_packages))
        m_keys.insert({pkg.packageId.isEmpty() ? pkg.name : pkg.packageId,
                       static_cast<int>(pkg.backend)});
    endResetModel();
}

void PackageModel::updatePackageStatus(const QString &nameOrId, Backend backend, Status status)
{
    for (int row = 0; row < m_packages.size(); ++row) {
        Package &pkg = m_packages[row];
        if ((pkg.name == nameOrId || pkg.packageId == nameOrId) && pkg.backend == backend) {
            pkg.status = status;
            if (status == Status::Installed)
                pkg.installed = true;
            Q_EMIT dataChanged(index(row), index(row));
            return;
        }
    }
}

void PackageModel::setPackageInstalled(const QString &nameOrId, Backend backend, bool installed,
                                       const QString &version)
{
    for (int row = 0; row < m_packages.size(); ++row) {
        Package &pkg = m_packages[row];
        if ((pkg.name == nameOrId || pkg.packageId == nameOrId) && pkg.backend == backend) {
            pkg.installed = installed;
            pkg.status = installed ? Status::Installed : Status::Idle;
            if (!version.isEmpty())
                pkg.version = version;
            Q_EMIT dataChanged(index(row), index(row));
            return;
        }
    }
}

void PackageModel::setSelected(const QString &nameOrId, Backend backend, bool selected)
{
    for (int row = 0; row < m_packages.size(); ++row) {
        Package &pkg = m_packages[row];
        if ((pkg.name == nameOrId || pkg.packageId == nameOrId) && pkg.backend == backend) {
            pkg.selected = selected;
            Q_EMIT dataChanged(index(row), index(row));
            return;
        }
    }
}

void PackageModel::clearSelection()
{
    for (int row = 0; row < m_packages.size(); ++row) {
        if (m_packages[row].selected) {
            m_packages[row].selected = false;
            Q_EMIT dataChanged(index(row), index(row));
        }
    }
}

QVector<PackageModel::Package> PackageModel::selectedPackages() const
{
    QVector<Package> result;
    for (const Package &pkg : std::as_const(m_packages))
        if (pkg.selected)
            result.append(pkg);
    return result;
}

int PackageModel::selectedCount() const
{
    int count = 0;
    for (const Package &pkg : std::as_const(m_packages))
        if (pkg.selected)
            ++count;
    return count;
}

int PackageModel::updateCount() const
{
    int count = 0;
    for (const Package &pkg : std::as_const(m_packages))
        if (pkg.status == Status::NeedsUpdate)
            ++count;
    return count;
}

int PackageModel::updateCount(Backend backend) const
{
    int count = 0;
    for (const Package &pkg : std::as_const(m_packages))
        if (pkg.status == Status::NeedsUpdate && pkg.backend == backend)
            ++count;
    return count;
}

QString PackageModel::backendName(Backend backend)
{
    switch (backend) {
    case Backend::Pacman:
        return QStringLiteral("pacman");
    case Backend::Flatpak:
        return QStringLiteral("flatpak");
    case Backend::Cargo:
        return QStringLiteral("cargo");
    case Backend::Mise:
        return QStringLiteral("mise");
    case Backend::Rustup:
        return QStringLiteral("rustup");
    case Backend::Fvm:
        return QStringLiteral("fvm");
    }
    return {};
}

QString PackageModel::statusText(Status status)
{
    switch (status) {
    case Status::Idle:
        return QString();
    case Status::Installed:
        return i18n("Installed");
    case Status::Updating:
        return i18n("Updating…");
    case Status::Removing:
        return i18n("Removing…");
    case Status::Failed:
        return i18n("Failed");
    case Status::NeedsUpdate:
        return i18n("Update available");
    }
    return {};
}