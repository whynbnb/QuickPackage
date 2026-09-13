#include "MainWindow.h"
#include "DetailPanel.h"
#include "PackageCardDelegate.h"
#include "ProxyDialog.h"
#include "SettingsDialog.h"
#include "UpdateDialog.h"

#include <KActionCollection>
#include <KColorSchemeManager>
#include <KConfigGroup>
#include <KLocalizedString>
#include <KNotification>
#include <KSharedConfig>
#include <KStandardAction>
#include <QButtonGroup>
#include <QCloseEvent>
#include <QDialogButtonBox>
#include <QLineEdit>
#include <QListView>
#include <QMessageBox>
#include <QProgressBar>
#include <QPushButton>
#include <QSortFilterProxyModel>
#include <QStatusBar>
#include <QTimer>
#include <QToolButton>
#include <QVBoxLayout>

using Package = PackageModel::Package;

namespace {
// Must not be -1: QButtonGroup::addButton(btn, -1) means "auto-assign id"
// (negative ids starting at -2), so the All button would never get -1 and
// checkedId() would return -2, filtering out every row.
constexpr int BackendAll = 3;

QString backendLabel(PackageModel::Backend backend)
{
    switch (backend) {
    case PackageModel::Backend::Pacman:
        return i18n("Pacman");
    case PackageModel::Backend::Flatpak:
        return i18n("Flatpak");
    case PackageModel::Backend::Cargo:
        return i18n("Cargo");
    }
    return {};
}
} // namespace

class MainWindow::PackageFilterProxyModel : public QSortFilterProxyModel
{
public:
    void applyFilter(const QString &text, int backendFilter, bool updatesOnlyFilter)
    {
        beginFilterChange();
        searchText = text;
        backend = backendFilter;
        updatesOnly = updatesOnlyFilter;
        endFilterChange(QSortFilterProxyModel::Direction::Rows);
    }

    QString searchText;
    int backend = BackendAll;
    bool updatesOnly = false;

    bool filterAcceptsRow(int sourceRow, const QModelIndex &sourceParent) const override
    {
        const QModelIndex index = sourceModel()->index(sourceRow, 0, sourceParent);
        if (backend != BackendAll
            && index.data(PackageModel::BackendRole).toInt() != backend)
            return false;
        if (updatesOnly
            && static_cast<PackageModel::Status>(
                   index.data(PackageModel::StatusRole).toInt())
                != PackageModel::Status::NeedsUpdate)
            return false;
        if (searchText.isEmpty())
            return true;
        const QString name = index.data(PackageModel::NameRole).toString();
        const QString packageId = index.data(PackageModel::PackageIdRole).toString();
        const QString description = index.data(PackageModel::DescriptionRole).toString();
        return name.contains(searchText, Qt::CaseInsensitive)
            || packageId.contains(searchText, Qt::CaseInsensitive)
            || description.contains(searchText, Qt::CaseInsensitive);
    }
};

MainWindow::MainWindow(QWidget *parent)
    : KXmlGuiWindow(parent)
    , m_manager(this)
    , m_proxy(this)
{
    m_proxy.load();
    m_model = new PackageModel(this);
    m_filterProxy = new PackageFilterProxyModel;
    m_filterProxy->setSourceModel(m_model);

    setupUi();
    setupActions();
    setupGUI();

    connect(&m_manager, &PackageManager::started, this, [this](const QString &message) {
        setBusy(true, message);
    });
    connect(&m_manager, &PackageManager::progress, this,
            [this](const QString &message) { m_statusLabel->setText(message); });
    connect(&m_manager, &PackageManager::packagesLoaded, this,
            [this](const QVector<Package> &packages) {
                m_model->setPackages(packages);
                if (m_updatesLoaded) {
                    for (const QString &name : std::as_const(m_pacmanUpdates))
                        m_model->updatePackageStatus(name, PackageModel::Backend::Pacman,
                                                     PackageModel::Status::NeedsUpdate);
                    for (const QString &name : std::as_const(m_flatpakUpdates))
                        m_model->updatePackageStatus(name, PackageModel::Backend::Flatpak,
                                                     PackageModel::Status::NeedsUpdate);
                    for (const QString &name : std::as_const(m_cargoUpdates))
                        m_model->updatePackageStatus(name, PackageModel::Backend::Cargo,
                                                     PackageModel::Status::NeedsUpdate);
                }
                updateCounters();
            });
    connect(&m_manager, &PackageManager::updatesReady, this,
            [this](const QSet<QString> &pacman, const QSet<QString> &flatpak,
                   const QSet<QString> &cargo) {
                m_pacmanUpdates = pacman;
                m_flatpakUpdates = flatpak;
                m_cargoUpdates = cargo;
                m_updatesLoaded = true;

                for (const QString &name : std::as_const(m_pacmanUpdates))
                    m_model->updatePackageStatus(name, PackageModel::Backend::Pacman,
                                                 PackageModel::Status::NeedsUpdate);
                for (const QString &name : std::as_const(m_flatpakUpdates))
                    m_model->updatePackageStatus(name, PackageModel::Backend::Flatpak,
                                                 PackageModel::Status::NeedsUpdate);
                for (const QString &name : std::as_const(m_cargoUpdates))
                    m_model->updatePackageStatus(name, PackageModel::Backend::Cargo,
                                                 PackageModel::Status::NeedsUpdate);

                const int count = m_model->updateCount();
                m_updatesButton->setEnabled(count > 0);
                updateCounters();
                if (count > 0) {
                    // Avoid repeating the notification when nothing changed
                    if (count != m_lastNotifiedUpdates) {
                        notify(QStringLiteral("update-available"),
                               i18n("Updates available"),
                               i18np("1 update is available", "%1 updates are available",
                                     count));
                    }
                    m_statusLabel->setText(i18n("%1 updates available", count));
                } else {
                    m_statusLabel->setText(i18n("All packages are up to date"));
                }
                m_lastNotifiedUpdates = count;
            });
    connect(&m_manager, &PackageManager::finished, this, [this](bool ok, const QString &message) {
        setBusy(false);
        if (!message.isEmpty())
            m_statusLabel->setText(message);
        // Only notify for user-initiated operations (install/remove);
        // background refreshes stay silent in the status bar.
        if (m_inUserOperation) {
            m_inUserOperation = false;
            if (ok)
                notify(QStringLiteral("operation-done"), i18n("Operation finished"), message);
            else
                notify(QStringLiteral("operation-failed"), i18n("Operation failed"), message);
            // Refresh the lists so the new state is reflected immediately
            m_manager.refreshCache();
            m_manager.refreshUpdates();
        }
    });

    KConfigGroup group =
        KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))->group(QStringLiteral("General"));

    // Apply the saved color scheme (system / light / dark)
    m_schemeManager = KColorSchemeManager::instance();
    const QString theme = group.readEntry(QStringLiteral("Theme"), QStringLiteral("system"));
    if (theme == QLatin1String("light"))
        m_schemeManager->activateSchemeId(QStringLiteral("BreezeLight"));
    else if (theme == QLatin1String("dark"))
        m_schemeManager->activateSchemeId(QStringLiteral("BreezeDark"));
    else
        m_schemeManager->activateSchemeId(QString());

    m_loadOnStart = group.readEntry("CheckUpdatesOnStart", true);
    QTimer::singleShot(600, this, [this] {
        m_manager.refreshCache();
        if (m_loadOnStart)
            m_manager.refreshUpdates();
    });
}

MainWindow::~MainWindow() = default;

void MainWindow::setupUi()
{
    auto *central = new QWidget(this);
    auto *layout = new QVBoxLayout(central);
    layout->setContentsMargins(12, 12, 12, 12);
    layout->setSpacing(10);

    auto *topBar = new QHBoxLayout;
    topBar->setSpacing(8);

    m_searchEdit = new QLineEdit(central);
    m_searchEdit->setClearButtonEnabled(true);
    m_searchEdit->setPlaceholderText(i18n("Search packages…"));
    topBar->addWidget(m_searchEdit, 1);

    m_refreshButton = new QPushButton(i18n("Refresh"), central);
    m_refreshButton->setIcon(QIcon::fromTheme(QStringLiteral("view-refresh")));
    topBar->addWidget(m_refreshButton);

    m_updatesButton = new QPushButton(i18n("Updates"), central);
    m_updatesButton->setIcon(QIcon::fromTheme(QStringLiteral("update-none")));
    topBar->addWidget(m_updatesButton);

    layout->addLayout(topBar);

    // Flat category chips: always-visible, one-click source filtering
    auto *chipsRow = new QHBoxLayout;
    chipsRow->setSpacing(6);
    m_backendGroup = new QButtonGroup(this);
    m_backendGroup->setExclusive(true);
    auto makeChip = [this, central, &chipsRow](const QString &text, int id) {
        auto *chip = new QToolButton(central);
        chip->setText(text);
        chip->setCheckable(true);
        chip->setAutoRaise(true);
        chip->setToolButtonStyle(Qt::ToolButtonTextOnly);
        chipsRow->addWidget(chip);
        m_backendGroup->addButton(chip, id);
        return chip;
    };
    m_btnAll = makeChip(i18n("All"), BackendAll);
    m_btnPacman = makeChip(backendLabel(PackageModel::Backend::Pacman),
                           int(PackageModel::Backend::Pacman));
    m_btnFlatpak = makeChip(backendLabel(PackageModel::Backend::Flatpak),
                            int(PackageModel::Backend::Flatpak));
    m_btnCargo = makeChip(backendLabel(PackageModel::Backend::Cargo),
                          int(PackageModel::Backend::Cargo));
    m_btnAll->setChecked(true);
    chipsRow->addStretch();

    // Updates filter chip: click to show only packages with pending updates
    m_btnUpdates = new QToolButton(central);
    m_btnUpdates->setCheckable(true);
    m_btnUpdates->setAutoRaise(true);
    m_btnUpdates->setToolButtonStyle(Qt::ToolButtonTextOnly);
    m_btnUpdates->setEnabled(false);
    chipsRow->addWidget(m_btnUpdates);
    layout->addLayout(chipsRow);

    m_listView = new QListView(central);
    m_listView->setModel(m_filterProxy);
    m_listView->setItemDelegate(new PackageCardDelegate(m_listView));
    m_listView->setSelectionMode(QAbstractItemView::NoSelection);
    m_listView->setUniformItemSizes(false);
    m_listView->setMouseTracking(true);
    m_listView->setVerticalScrollMode(QAbstractItemView::ScrollPerPixel);

    // Master-detail: click a card on the left to inspect and act on it
    m_detailPanel = new DetailPanel(central);
    m_detailPanel->setProxyEnvironment(m_manager.operationEnvironment());
    m_detailPanel->setFixedWidth(330);
    auto *split = new QHBoxLayout;
    split->setSpacing(10);
    split->addWidget(m_listView, 1);
    split->addWidget(m_detailPanel);
    layout->addLayout(split, 1);

    auto *bottomBar = new QHBoxLayout;
    bottomBar->setSpacing(8);

    m_installButton = new QPushButton(i18n("Install"), central);
    m_installButton->setIcon(QIcon::fromTheme(QStringLiteral("list-add")));
    bottomBar->addWidget(m_installButton);

    m_removeButton = new QPushButton(i18n("Remove"), central);
    m_removeButton->setIcon(QIcon::fromTheme(QStringLiteral("list-remove")));
    bottomBar->addWidget(m_removeButton);

    m_countLabel = new QLabel(central);
    m_countLabel->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    bottomBar->addStretch();
    bottomBar->addWidget(m_countLabel);

    layout->addLayout(bottomBar);
    setCentralWidget(central);

    m_statusLabel = new QLabel(i18n("Ready"), this);
    statusBar()->addWidget(m_statusLabel, 1);

    m_progressBar = new QProgressBar(this);
    m_progressBar->setMaximumWidth(220);
    m_progressBar->setTextVisible(false);
    m_progressBar->setRange(0, 0);
    m_progressBar->hide();
    statusBar()->addPermanentWidget(m_progressBar);

    connect(m_searchEdit, &QLineEdit::textChanged, this,
            [this](const QString &) { refreshFilterProxy(); });
    connect(m_backendGroup, &QButtonGroup::idClicked, this,
            [this](int) { refreshFilterProxy(); });
    connect(m_btnUpdates, &QToolButton::toggled, this,
            [this](bool) { refreshFilterProxy(); });
    connect(m_refreshButton, &QPushButton::clicked, this,
            [this] { m_manager.refreshCache(); });
    connect(m_updatesButton, &QPushButton::clicked, this,
            [this] { m_manager.refreshUpdates(); });
    connect(m_installButton, &QPushButton::clicked, this,
            [this] { installSelected(); });
    connect(m_removeButton, &QPushButton::clicked, this,
            [this] { removeSelected(); });
    connect(m_listView, &QListView::clicked, this, [this](const QModelIndex &proxyIndex) {
        const QModelIndex index = m_filterProxy->mapToSource(proxyIndex);
        m_detailPanel->setPackage(m_model->packageAt(index.row()));
    });
    connect(m_detailPanel, &DetailPanel::installRequested, this,
            [this](const Package &pkg) { installPackage(pkg); });
    connect(m_detailPanel, &DetailPanel::uninstallRequested, this,
            [this](const Package &pkg) { uninstallPackage(pkg); });
    connect(m_detailPanel, &DetailPanel::updateRequested, this,
            [this](const Package &pkg) { updatePackage(pkg); });
    connect(m_model, &PackageModel::dataChanged, this, [this] { updateCounters(); });
    connect(m_model, &PackageModel::modelReset, this, [this] { updateCounters(); });

    auto *delegate = static_cast<PackageCardDelegate *>(m_listView->itemDelegate());
    connect(delegate, &PackageCardDelegate::selectionToggled, this,
            [this](const QModelIndex &proxyIndex, bool checked) {
                const QModelIndex index = m_filterProxy->mapToSource(proxyIndex);
                m_model->setSelected(index.data(PackageModel::NameRole).toString(),
                                     static_cast<PackageModel::Backend>(
                                         index.data(PackageModel::BackendRole).toInt()),
                                     checked);
            });
}

void MainWindow::setupActions()
{
    KActionCollection *ac = actionCollection();

    QAction *refresh = ac->addAction(QStringLiteral("refresh_all"));
    refresh->setText(i18n("Refresh Lists"));
    refresh->setIcon(QIcon::fromTheme(QStringLiteral("view-refresh")));
    ac->setDefaultShortcut(refresh, QKeySequence(Qt::Key_F5));
    connect(refresh, &QAction::triggered, this, [this] { m_manager.refreshCache(); });

    QAction *checkUpdates = ac->addAction(QStringLiteral("check_updates"));
    checkUpdates->setText(i18n("Check for Updates"));
    checkUpdates->setIcon(QIcon::fromTheme(QStringLiteral("update-none")));
    ac->setDefaultShortcut(checkUpdates, QKeySequence(QStringLiteral("Ctrl+U")));
    connect(checkUpdates, &QAction::triggered, this,
            [this] { m_manager.refreshUpdates(); });

    QAction *installAction = ac->addAction(QStringLiteral("install_selected"));
    installAction->setText(i18n("Install Selected"));
    installAction->setIcon(QIcon::fromTheme(QStringLiteral("list-add")));
    ac->setDefaultShortcut(installAction, QKeySequence(QStringLiteral("Ctrl+I")));
    connect(installAction, &QAction::triggered, this, [this] { installSelected(); });

    QAction *removeAction = ac->addAction(QStringLiteral("remove_selected"));
    removeAction->setText(i18n("Remove Selected"));
    removeAction->setIcon(QIcon::fromTheme(QStringLiteral("list-remove")));
    ac->setDefaultShortcut(removeAction, QKeySequence(Qt::Key_Delete));
    connect(removeAction, &QAction::triggered, this, [this] { removeSelected(); });

    QAction *proxyAction = ac->addAction(QStringLiteral("proxy_settings"));
    proxyAction->setText(i18n("Configure Proxy…"));
    proxyAction->setIcon(QIcon::fromTheme(QStringLiteral("network-proxy")));
    connect(proxyAction, &QAction::triggered, this, [this] { showProxyDialog(); });

    QAction *settingsAction = ac->addAction(QStringLiteral("preferences"));
    settingsAction->setText(i18n("Preferences…"));
    settingsAction->setIcon(QIcon::fromTheme(QStringLiteral("configure")));
    ac->setDefaultShortcut(settingsAction, QKeySequence(QStringLiteral("Ctrl+Shift+,")));
    connect(settingsAction, &QAction::triggered, this, [this] { showSettingsDialog(); });

    QAction *quit = KStandardAction::quit(qApp, &QCoreApplication::quit, ac);
    Q_UNUSED(quit);
}

void MainWindow::refreshFilterProxy()
{
    m_filterProxy->applyFilter(m_searchEdit->text().trimmed(),
                               m_backendGroup->checkedId(),
                               m_btnUpdates->isChecked());
    // Reset the scroll position so a smaller result set is never shown blank
    m_listView->scrollToTop();
}

void MainWindow::updateCounters()
{
    const int selected = m_model->selectedCount();
    const int updates = m_model->updateCount();
    m_countLabel->setText(i18n("%1 selected · %2 updates", selected, updates));
    m_installButton->setEnabled(selected > 0);
    m_removeButton->setEnabled(selected > 0);
    m_updatesButton->setEnabled(updates > 0);

    // Live per-category counts (total + pending updates) so sources and
    // pending updates are visible at a glance
    int pacman = 0, flatpak = 0, cargo = 0;
    const int rows = m_model->rowCount();
    for (int row = 0; row < rows; ++row) {
        switch (static_cast<PackageModel::Backend>(
            m_model->index(row).data(PackageModel::BackendRole).toInt())) {
        case PackageModel::Backend::Pacman:
            ++pacman;
            break;
        case PackageModel::Backend::Flatpak:
            ++flatpak;
            break;
        case PackageModel::Backend::Cargo:
            ++cargo;
            break;
        }
    }
    auto chipText = [](const QString &label, int total, int updates) {
        if (updates > 0)
            return i18n("%1 (%2, %3 updates)", label, total, updates);
        return i18n("%1 (%2)", label, total);
    };
    m_btnAll->setText(chipText(i18n("All"), pacman + flatpak + cargo, updates));
    m_btnPacman->setText(chipText(i18n("Pacman"), pacman,
                                  m_model->updateCount(PackageModel::Backend::Pacman)));
    m_btnFlatpak->setText(chipText(i18n("Flatpak"), flatpak,
                                   m_model->updateCount(PackageModel::Backend::Flatpak)));
    m_btnCargo->setText(chipText(i18n("Cargo"), cargo,
                                 m_model->updateCount(PackageModel::Backend::Cargo)));

    m_btnUpdates->setText(i18n("Updates (%1)", updates));
    QFont chipFont = m_btnUpdates->font();
    chipFont.setBold(updates > 0);
    m_btnUpdates->setFont(chipFont);
    if (updates > 0) {
        m_btnUpdates->setEnabled(true);
    } else {
        m_btnUpdates->setChecked(false);
        m_btnUpdates->setEnabled(false);
    }
}

void MainWindow::setBusy(bool busy, const QString &message)
{
    if (busy) {
        ++m_busyDepth;
        m_progressBar->show();
        if (!message.isEmpty())
            m_statusLabel->setText(message);
    } else {
        m_busyDepth = qMax(0, m_busyDepth - 1);
        if (m_busyDepth == 0)
            m_progressBar->hide();
    }
    const bool locked = m_busyDepth > 0;
    m_listView->setEnabled(!locked);
    m_refreshButton->setEnabled(!locked);
    m_searchEdit->setEnabled(!locked);
    m_btnAll->setEnabled(!locked);
    m_btnPacman->setEnabled(!locked);
    m_btnFlatpak->setEnabled(!locked);
    m_btnCargo->setEnabled(!locked);
    m_btnUpdates->setEnabled(!locked);
    if (locked) {
        m_installButton->setEnabled(false);
        m_removeButton->setEnabled(false);
        m_updatesButton->setEnabled(false);
    } else {
        updateCounters();
    }
}

void MainWindow::installSelected()
{
    const QVector<Package> packages = m_model->selectedPackages();
    if (packages.isEmpty())
        return;
    QStringList names;
    for (const Package &pkg : packages)
        names << pkg.name;
    const auto answer = QMessageBox::question(
        this, i18n("Install Packages"),
        i18np("Install the following package?", "Install the following %1 packages?",
              packages.size())
            + QStringLiteral("\n\n") + names.join(QStringLiteral(", ")),
        QMessageBox::Yes | QMessageBox::Cancel, QMessageBox::Yes);
    if (answer != QMessageBox::Yes)
        return;
    m_inUserOperation = true;
    m_manager.install(packages);
}

void MainWindow::removeSelected()
{
    const QVector<Package> packages = m_model->selectedPackages();
    if (packages.isEmpty())
        return;
    QStringList names;
    for (const Package &pkg : packages)
        names << pkg.name;
    const auto answer = QMessageBox::question(
        this, i18n("Remove Packages"),
        i18np("Remove the following package?", "Remove the following %1 packages?",
              packages.size())
            + QStringLiteral("\n\n") + names.join(QStringLiteral(", ")),
        QMessageBox::Yes | QMessageBox::Cancel, QMessageBox::Cancel);
    if (answer != QMessageBox::Yes)
        return;
    m_inUserOperation = true;
    m_manager.remove(packages);
}

void MainWindow::installPackage(const Package &pkg)
{
    const auto answer = QMessageBox::question(this, i18n("Install Package"),
                                              i18n("Install %1?", pkg.name),
                                              QMessageBox::Yes | QMessageBox::Cancel,
                                              QMessageBox::Yes);
    if (answer != QMessageBox::Yes)
        return;
    m_inUserOperation = true;
    m_manager.install({pkg});
}

void MainWindow::uninstallPackage(const Package &pkg)
{
    const auto answer = QMessageBox::question(this, i18n("Uninstall Package"),
                                              i18n("Uninstall %1?", pkg.name),
                                              QMessageBox::Yes | QMessageBox::Cancel,
                                              QMessageBox::Cancel);
    if (answer != QMessageBox::Yes)
        return;
    m_inUserOperation = true;
    m_manager.remove({pkg});
}

void MainWindow::updatePackage(const Package &pkg)
{
    QString note;
    if (pkg.backend == PackageModel::Backend::Pacman) {
        note = i18n("Pacman updates always upgrade the whole system first"
                    " (pacman -Syu) to avoid partial updates.");
    }
    const auto answer = QMessageBox::question(
        this, i18n("Update Package"),
        i18n("Update %1?", pkg.name)
            + (note.isEmpty() ? QString() : QStringLiteral("\n\n") + note),
        QMessageBox::Yes | QMessageBox::Cancel, QMessageBox::Yes);
    if (answer != QMessageBox::Yes)
        return;
    m_inUserOperation = true;
    m_manager.update({pkg});
}

void MainWindow::showUpdateDialog()
{
    QVector<Package> updates;
    const int rows = m_model->rowCount();
    for (int row = 0; row < rows; ++row) {
        const QModelIndex index = m_model->index(row);
        if (static_cast<PackageModel::Status>(index.data(PackageModel::StatusRole).toInt())
            == PackageModel::Status::NeedsUpdate) {
            Package pkg;
            pkg.name = index.data(PackageModel::NameRole).toString();
            pkg.packageId = index.data(PackageModel::PackageIdRole).toString();
            pkg.version = index.data(PackageModel::VersionRole).toString();
            pkg.newVersion = index.data(PackageModel::NewVersionRole).toString();
            pkg.backend = static_cast<PackageModel::Backend>(
                index.data(PackageModel::BackendRole).toInt());
            updates.append(pkg);
        }
    }

    UpdateDialog dialog(updates, this);
    if (dialog.exec() == QDialog::Accepted) {
        const QVector<Package> checked = dialog.checkedPackages();
        if (checked.isEmpty())
            return;
        m_inUserOperation = true;
        m_manager.update(checked);
    }
}

void MainWindow::showProxyDialog()
{
    ProxyDialog dialog(&m_proxy, this);
    if (dialog.exec() == QDialog::Accepted) {
        const QString url = m_proxy.url();
        m_manager.setProxy(url, url);
        m_detailPanel->setProxyEnvironment(m_manager.operationEnvironment());
        if (url.isEmpty())
            m_statusLabel->setText(i18n("Proxy disabled"));
        else
            m_statusLabel->setText(i18nc("@info:status Proxy summary", "Proxy: %1", url));
    }
}

void MainWindow::showSettingsDialog()
{
    SettingsDialog dialog(m_schemeManager, this);
    dialog.exec();
}

void MainWindow::notify(const QString &eventId, const QString &title, const QString &text)
{
    KNotification::event(eventId, title, text, QStringLiteral("quickpackage"),
                         KNotification::CloseOnTimeout, QStringLiteral("quickpackage"));
}

void MainWindow::closeEvent(QCloseEvent *event)
{
    if (m_busyDepth > 0) {
        const auto answer = QMessageBox::question(
            this, i18n("Operation in Progress"),
            i18n("An operation is still running. Quit anyway?"),
            QMessageBox::Yes | QMessageBox::No, QMessageBox::No);
        if (answer != QMessageBox::Yes) {
            event->ignore();
            return;
        }
        m_manager.cancel();
    }
    event->accept();
}
