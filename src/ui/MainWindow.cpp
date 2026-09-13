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
#include <QStackedWidget>
#include <QStatusBar>
#include <QTimer>
#include <QToolButton>
#include <QVBoxLayout>

using Package = PackageModel::Package;

namespace {
    // Must not be -1: QButtonGroup::addButton(btn, -1) means "auto-assign id"
    // (negative ids starting at -2), so the All button would never get -1 and
    // checkedId() would return -2, filtering out every row. The pseudo-filter
    // ids sit above every PackageModel::Backend value.
    constexpr int BackendAll = 100;
    // AUR pseudo-filter: foreign (AUR/local) pacman packages.
    constexpr int BackendAur = 101;

    QString backendLabel(PackageModel::Backend backend)
    {
        switch (backend) {
        case PackageModel::Backend::Pacman:
            return i18n("Pacman");
        case PackageModel::Backend::Flatpak:
            return i18n("Flatpak");
        case PackageModel::Backend::Cargo:
            return i18n("Cargo");
        case PackageModel::Backend::Mise:
            return i18n("Mise");
        case PackageModel::Backend::Rustup:
            return i18n("Rustup");
        case PackageModel::Backend::Fvm:
            return i18n("FVM");
        }
        return {};
    }
} // namespace

class MainWindow::PackageFilterProxyModel : public QSortFilterProxyModel
{
public:
    void applyFilter(const QString &text, int backendFilter, bool updatesOnlyFilter,
                     bool devModeFilter)
    {
        beginFilterChange();
        searchText = text;
        backend = backendFilter;
        updatesOnly = updatesOnlyFilter;
        devMode = devModeFilter;
        endFilterChange(QSortFilterProxyModel::Direction::Rows);
    }

    QString searchText;
    int backend = BackendAll;
    bool updatesOnly = false;
    // Big switch: system packages vs developer tools
    bool devMode = false;

    bool filterAcceptsRow(int sourceRow, const QModelIndex &sourceParent) const override
    {
        const QModelIndex index = sourceModel()->index(sourceRow, 0, sourceParent);
        const auto rowBackend = static_cast<PackageModel::Backend>(
            index.data(PackageModel::BackendRole).toInt());
        const bool foreign = index.data(PackageModel::IsForeignRole).toBool();
        // The big switch splits system backends from developer tools
        const bool devBackend = rowBackend == PackageModel::Backend::Mise
            || rowBackend == PackageModel::Backend::Rustup
            || rowBackend == PackageModel::Backend::Fvm;
        if (devMode ? !devBackend : devBackend)
            return false;
        if (backend == BackendAur) {
            // AUR pseudo-filter: foreign pacman packages
            if (rowBackend != PackageModel::Backend::Pacman || !foreign)
                return false;
        } else if (backend != BackendAll) {
            if (index.data(PackageModel::BackendRole).toInt() != backend)
                return false;
            // The pacman chip only lists repo packages; AUR has its own chip.
            if (rowBackend == PackageModel::Backend::Pacman && foreign)
                return false;
        }
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

protected:
    bool lessThan(const QModelIndex &left, const QModelIndex &right) const override
    {
        const QString a = left.data(PackageModel::NameRole).toString();
        const QString b = right.data(PackageModel::NameRole).toString();
        return a.compare(b, Qt::CaseInsensitive) < 0;
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
    m_filterProxy->sort(0, Qt::AscendingOrder);

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
                    for (auto it = m_updates.constBegin(); it != m_updates.constEnd(); ++it)
                        for (const QString &name : it.value())
                            m_model->updatePackageStatus(
                                name, static_cast<PackageModel::Backend>(it.key()),
                                PackageModel::Status::NeedsUpdate);
                }
                updateCounters();
            });
    connect(&m_manager, &PackageManager::updatesReady, this,
            [this](const QHash<int, QSet<QString>> &updates) {
                m_updates = updates;
                m_updatesLoaded = true;

                for (auto it = m_updates.constBegin(); it != m_updates.constEnd(); ++it)
                    for (const QString &name : it.value())
                        m_model->updatePackageStatus(
                            name, static_cast<PackageModel::Backend>(it.key()),
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
    loadBackendSettings();
    applyEnabledBackends();
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

    // Big mode switch: system packages vs developer tools
    m_modeGroup = new QButtonGroup(this);
    m_modeGroup->setExclusive(true);
    m_btnSystem = new QToolButton(central);
    m_btnSystem->setText(i18n("System packages"));
    m_btnSystem->setIcon(QIcon::fromTheme(QStringLiteral("applications-system")));
    m_btnSystem->setToolButtonStyle(Qt::ToolButtonTextBesideIcon);
    m_btnSystem->setCheckable(true);
    m_btnSystem->setChecked(true);
    QFont modeFont = m_btnSystem->font();
    modeFont.setBold(true);
    m_btnSystem->setFont(modeFont);
    m_modeGroup->addButton(m_btnSystem, 0);
    topBar->insertWidget(0, m_btnSystem);

    m_btnDev = new QToolButton(central);
    m_btnDev->setText(i18n("Developer packages"));
    m_btnDev->setIcon(QIcon::fromTheme(QStringLiteral("applications-development")));
    m_btnDev->setToolButtonStyle(Qt::ToolButtonTextBesideIcon);
    m_btnDev->setCheckable(true);
    m_btnDev->setFont(modeFont);
    // The developer side only exists when a dev backend is enabled in Settings
    m_btnDev->setVisible(false);
    m_modeGroup->addButton(m_btnDev, 1);
    topBar->insertWidget(1, m_btnDev);

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
    m_btnAur = makeChip(i18n("AUR"), BackendAur);
    m_btnFlatpak = makeChip(backendLabel(PackageModel::Backend::Flatpak),
                            int(PackageModel::Backend::Flatpak));
    m_btnCargo = makeChip(backendLabel(PackageModel::Backend::Cargo),
                          int(PackageModel::Backend::Cargo));

    // Developer tool chips: only visible in developer mode
    m_btnMise = makeChip(backendLabel(PackageModel::Backend::Mise),
                         int(PackageModel::Backend::Mise));
    m_btnRustup = makeChip(backendLabel(PackageModel::Backend::Rustup),
                           int(PackageModel::Backend::Rustup));
    m_btnFvm = makeChip(backendLabel(PackageModel::Backend::Fvm),
                        int(PackageModel::Backend::Fvm));
    m_btnMise->setVisible(false);
    m_btnRustup->setVisible(false);
    m_btnFvm->setVisible(false);
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

    // Hint page shown when every package backend is disabled
    m_backendsHint = new QLabel(i18n("All package backends are disabled."
                                     " Enable at least one in Settings."),
                                central);
    m_backendsHint->setAlignment(Qt::AlignCenter);
    m_backendsHint->setWordWrap(true);
    m_listStack = new QStackedWidget(central);
    m_listStack->addWidget(m_listView);
    m_listStack->addWidget(m_backendsHint);

    // Master-detail: click a card on the left to inspect and act on it
    m_detailPanel = new DetailPanel(central);
    m_detailPanel->setProxyEnvironment(m_manager.operationEnvironment());
    m_detailPanel->setFixedWidth(330);
    auto *split = new QHBoxLayout;
    split->setSpacing(10);
    split->addWidget(m_listStack, 1);
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
    connect(m_modeGroup, &QButtonGroup::idClicked, this,
            [this](int id) { setDevMode(id == 1); });
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
                               m_btnUpdates->isChecked(), m_devMode);
    // Reset the scroll position so a smaller result set is never shown blank
    m_listView->scrollToTop();
}

void MainWindow::setDevMode(bool dev)
{
    if (m_devMode == dev)
        return;
    m_devMode = dev;
    refreshModeUi();
    updateCounters();
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
    // pending updates are visible at a glance. AUR (foreign pacman) packages
    // get their own chip, so the pacman chip excludes them.
    int pacman = 0, aur = 0, flatpak = 0, cargo = 0, aurUpdates = 0;
    int mise = 0, rustup = 0, fvm = 0, devUpdates = 0;
    const int rows = m_model->rowCount();
    for (int row = 0; row < rows; ++row) {
        const QModelIndex index = m_model->index(row);
        const bool foreign = index.data(PackageModel::IsForeignRole).toBool();
        const bool needsUpdate = static_cast<PackageModel::Status>(
            index.data(PackageModel::StatusRole).toInt())
            == PackageModel::Status::NeedsUpdate;
        switch (static_cast<PackageModel::Backend>(
            index.data(PackageModel::BackendRole).toInt())) {
        case PackageModel::Backend::Pacman:
            if (foreign) {
                ++aur;
                if (needsUpdate)
                    ++aurUpdates;
            } else {
                ++pacman;
            }
            break;
        case PackageModel::Backend::Flatpak:
            ++flatpak;
            break;
        case PackageModel::Backend::Cargo:
            ++cargo;
            break;
        case PackageModel::Backend::Mise:
            ++mise;
            if (needsUpdate)
                ++devUpdates;
            break;
        case PackageModel::Backend::Rustup:
            ++rustup;
            if (needsUpdate)
                ++devUpdates;
            break;
        case PackageModel::Backend::Fvm:
            ++fvm;
            if (needsUpdate)
                ++devUpdates;
            break;
        }
    }
    auto chipText = [](const QString &label, int total, int updates) {
        if (updates > 0)
            return i18n("%1 (%2, %3 updates)", label, total, updates);
        return i18n("%1 (%2)", label, total);
    };
    // The All chip only counts the packages of the active mode
    if (m_devMode)
        m_btnAll->setText(chipText(i18n("All"), mise + rustup + fvm, devUpdates));
    else
        m_btnAll->setText(chipText(i18n("All"), pacman + aur + flatpak + cargo,
                                   updates - devUpdates));
    m_btnPacman->setText(chipText(i18n("Pacman"), pacman,
                                  m_model->updateCount(PackageModel::Backend::Pacman)
                                      - aurUpdates));
    m_btnAur->setText(chipText(i18n("AUR"), aur, aurUpdates));
    m_btnFlatpak->setText(chipText(i18n("Flatpak"), flatpak,
                                   m_model->updateCount(PackageModel::Backend::Flatpak)));
    m_btnCargo->setText(chipText(i18n("Cargo"), cargo,
                                 m_model->updateCount(PackageModel::Backend::Cargo)));
    m_btnMise->setText(chipText(i18n("Mise"), mise,
                                m_model->updateCount(PackageModel::Backend::Mise)));
    m_btnRustup->setText(chipText(i18n("Rustup"), rustup,
                                  m_model->updateCount(PackageModel::Backend::Rustup)));
    m_btnFvm->setText(chipText(i18n("FVM"), fvm,
                               m_model->updateCount(PackageModel::Backend::Fvm)));

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
    const bool hasBackend = m_pacmanEnabled || m_flatpakEnabled || m_cargoEnabled;
    m_listView->setEnabled(!locked);
    m_refreshButton->setEnabled(!locked && hasBackend);
    m_searchEdit->setEnabled(!locked);
    m_btnAll->setEnabled(!locked);
    m_btnPacman->setEnabled(!locked);
    m_btnAur->setEnabled(!locked);
    m_btnFlatpak->setEnabled(!locked);
    m_btnCargo->setEnabled(!locked);
    m_btnMise->setEnabled(!locked);
    m_btnRustup->setEnabled(!locked);
    m_btnFvm->setEnabled(!locked);
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
            pkg.foreign = index.data(PackageModel::IsForeignRole).toBool();
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
    connect(&dialog, &SettingsDialog::backendsChanged, this, [this] {
        const bool oldPacman = m_pacmanEnabled;
        const bool oldFlatpak = m_flatpakEnabled;
        const bool oldCargo = m_cargoEnabled;
        const bool oldMise = m_miseEnabled;
        const bool oldRustup = m_rustupEnabled;
        const bool oldFvm = m_fvmEnabled;
        loadBackendSettings();
        if (oldPacman == m_pacmanEnabled && oldFlatpak == m_flatpakEnabled
            && oldCargo == m_cargoEnabled && oldMise == m_miseEnabled
            && oldRustup == m_rustupEnabled && oldFvm == m_fvmEnabled)
            return;
        applyEnabledBackends();
        // Reload the package lists so the new backend set is reflected
        m_manager.refreshCache();
        m_manager.refreshUpdates();
    });
    dialog.exec();
}

void MainWindow::loadBackendSettings()
{
    const KConfigGroup group =
        KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
            ->group(QStringLiteral("General"));
    m_pacmanEnabled = group.readEntry("EnablePacman", true);
    m_flatpakEnabled = group.readEntry("EnableFlatpak", true);
    m_cargoEnabled = group.readEntry("EnableCargo", true);
    // Developer tool backends are optional and disabled by default.
    m_miseEnabled = group.readEntry("EnableMise", false);
    m_rustupEnabled = group.readEntry("EnableRustup", false);
    m_fvmEnabled = group.readEntry("EnableFvm", false);
}

void MainWindow::applyEnabledBackends()
{
    m_manager.setBackendEnabled(PackageModel::Backend::Pacman, m_pacmanEnabled);
    m_manager.setBackendEnabled(PackageModel::Backend::Flatpak, m_flatpakEnabled);
    m_manager.setBackendEnabled(PackageModel::Backend::Cargo, m_cargoEnabled);
    m_manager.setBackendEnabled(PackageModel::Backend::Mise, m_miseEnabled);
    m_manager.setBackendEnabled(PackageModel::Backend::Rustup, m_rustupEnabled);
    m_manager.setBackendEnabled(PackageModel::Backend::Fvm, m_fvmEnabled);

    refreshModeUi();
    updateCounters();
}

void MainWindow::refreshModeUi()
{
    const bool devAvailable = m_miseEnabled || m_rustupEnabled || m_fvmEnabled;
    const bool sysNone = !m_pacmanEnabled && !m_flatpakEnabled && !m_cargoEnabled;
    const bool devNone = !devAvailable;

    // Auto-switch: developer side only exists with an enabled dev backend;
    // if every system backend is disabled, jump to the developer side.
    if (m_devMode && devNone)
        m_devMode = false;
    if (sysNone && !devNone && !m_devMode)
        m_devMode = true;
    m_btnSystem->setChecked(!m_devMode);
    m_btnDev->setChecked(m_devMode);
    m_btnDev->setVisible(devAvailable);

    // Chips of the other mode stay hidden
    m_btnPacman->setVisible(!m_devMode && m_pacmanEnabled);
    m_btnAur->setVisible(!m_devMode && m_pacmanEnabled);
    m_btnFlatpak->setVisible(!m_devMode && m_flatpakEnabled);
    m_btnCargo->setVisible(!m_devMode && m_cargoEnabled);
    m_btnMise->setVisible(m_devMode && m_miseEnabled);
    m_btnRustup->setVisible(m_devMode && m_rustupEnabled);
    m_btnFvm->setVisible(m_devMode && m_fvmEnabled);

    // Hint page when the current mode has no enabled backend
    const bool none = m_devMode ? devNone : sysNone;
    m_listStack->setCurrentWidget(none ? static_cast<QWidget *>(m_backendsHint)
                                       : static_cast<QWidget *>(m_listView));
    m_refreshButton->setEnabled(!none);

    // Fall back to the All chip when the current chip is hidden (mode change
    // or backend disabled)
    const int current = m_backendGroup->checkedId();
    if ((current == int(PackageModel::Backend::Pacman)
             && (!m_pacmanEnabled || m_devMode))
        || (current == BackendAur && (!m_pacmanEnabled || m_devMode))
        || (current == int(PackageModel::Backend::Flatpak)
            && (!m_flatpakEnabled || m_devMode))
        || (current == int(PackageModel::Backend::Cargo)
            && (!m_cargoEnabled || m_devMode))
        || (current == int(PackageModel::Backend::Mise)
            && (!m_miseEnabled || !m_devMode))
        || (current == int(PackageModel::Backend::Rustup)
            && (!m_rustupEnabled || !m_devMode))
        || (current == int(PackageModel::Backend::Fvm)
            && (!m_fvmEnabled || !m_devMode)))
        m_btnAll->setChecked(true);
    refreshFilterProxy();
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
