#ifndef QUICKPACKAGE_MAINWINDOW_H
#define QUICKPACKAGE_MAINWINDOW_H

#include "PackageManager.h"
#include "PackageModel.h"
#include "ProxyManager.h"

#include <KXmlGuiWindow>
#include <QHash>
#include <QLabel>
#include <QSet>
#include <QSortFilterProxyModel>

class QButtonGroup;
class QLineEdit;
class QListView;
class QProgressBar;
class QPushButton;
class QStackedWidget;
class QToolButton;

class DetailPanel;
class KColorSchemeManager;

class MainWindow : public KXmlGuiWindow
{
    Q_OBJECT

public:
    explicit MainWindow(QWidget *parent = nullptr);
    ~MainWindow() override;

protected:
    void closeEvent(QCloseEvent *event) override;

private:
    class PackageFilterProxyModel;

    void setupActions();
    void setupUi();
    void refreshFilterProxy();
    void updateCounters();
    void setBusy(bool busy, const QString &message = {});
    void installSelected();
    void removeSelected();
    void installPackage(const PackageModel::Package &package);
    void uninstallPackage(const PackageModel::Package &package);
    void updatePackage(const PackageModel::Package &package);
    void showUpdateDialog();
    void showProxyDialog();
    void showSettingsDialog();
    void loadBackendSettings();
    void applyEnabledBackends();
    void refreshModeUi();
    void setDevMode(bool dev);
    void checkUpdatesOnStartup();
    void notify(const QString &eventId, const QString &title, const QString &text);

    PackageManager m_manager;
    ProxyManager m_proxy;
    PackageModel *m_model = nullptr;
    PackageFilterProxyModel *m_filterProxy = nullptr;

    QListView *m_listView = nullptr;
    QStackedWidget *m_listStack = nullptr;
    QLabel *m_backendsHint = nullptr;
    DetailPanel *m_detailPanel = nullptr;
    QLineEdit *m_searchEdit = nullptr;
    QButtonGroup *m_backendGroup = nullptr;
    QButtonGroup *m_modeGroup = nullptr;
    KColorSchemeManager *m_schemeManager = nullptr;
    QToolButton *m_btnSystem = nullptr;
    QToolButton *m_btnDev = nullptr;
    QToolButton *m_btnAll = nullptr;
    QToolButton *m_btnPacman = nullptr;
    QToolButton *m_btnAur = nullptr;
    QToolButton *m_btnFlatpak = nullptr;
    QToolButton *m_btnCargo = nullptr;
    QToolButton *m_btnMise = nullptr;
    QToolButton *m_btnRustup = nullptr;
    QToolButton *m_btnFvm = nullptr;
    QToolButton *m_btnUpdates = nullptr;
    QPushButton *m_refreshButton = nullptr;
    QPushButton *m_updatesButton = nullptr;
    QPushButton *m_installButton = nullptr;
    QPushButton *m_removeButton = nullptr;
    QLabel *m_statusLabel = nullptr;
    QLabel *m_countLabel = nullptr;
    QProgressBar *m_progressBar = nullptr;

    // Backend id (PackageModel::Backend) -> names with pending updates
    QHash<int, QSet<QString>> m_updates;
    bool m_updatesLoaded = false;
    bool m_loadOnStart = false;
    bool m_pacmanEnabled = true;
    bool m_flatpakEnabled = true;
    bool m_cargoEnabled = true;
    // Developer tool backends are optional and disabled by default.
    bool m_miseEnabled = false;
    bool m_rustupEnabled = false;
    bool m_fvmEnabled = false;
    // Big switch: false = system packages, true = developer tools
    bool m_devMode = false;
    int m_busyDepth = 0;
    bool m_inUserOperation = false;
    int m_lastNotifiedUpdates = -1;
};

#endif
