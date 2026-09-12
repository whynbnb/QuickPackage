#ifndef QUICKPACKAGE_MAINWINDOW_H
#define QUICKPACKAGE_MAINWINDOW_H

#include "PackageManager.h"
#include "PackageModel.h"
#include "ProxyManager.h"

#include <KXmlGuiWindow>
#include <QLabel>
#include <QSet>
#include <QSortFilterProxyModel>

class QButtonGroup;
class QLineEdit;
class QListView;
class QProgressBar;
class QPushButton;
class QToolButton;

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
    void showUpdateDialog();
    void showProxyDialog();
    void showSettingsDialog();
    void checkUpdatesOnStartup();
    void notify(const QString &eventId, const QString &title, const QString &text);

    PackageManager m_manager;
    ProxyManager m_proxy;
    PackageModel *m_model = nullptr;
    PackageFilterProxyModel *m_filterProxy = nullptr;

    QListView *m_listView = nullptr;
    QLineEdit *m_searchEdit = nullptr;
    QButtonGroup *m_backendGroup = nullptr;
    KColorSchemeManager *m_schemeManager = nullptr;
    QToolButton *m_btnAll = nullptr;
    QToolButton *m_btnPacman = nullptr;
    QToolButton *m_btnFlatpak = nullptr;
    QToolButton *m_btnCargo = nullptr;
    QPushButton *m_refreshButton = nullptr;
    QPushButton *m_updatesButton = nullptr;
    QPushButton *m_installButton = nullptr;
    QPushButton *m_removeButton = nullptr;
    QLabel *m_statusLabel = nullptr;
    QLabel *m_countLabel = nullptr;
    QProgressBar *m_progressBar = nullptr;

    QSet<QString> m_pacmanUpdates;
    QSet<QString> m_flatpakUpdates;
    QSet<QString> m_cargoUpdates;
    bool m_updatesLoaded = false;
    bool m_loadOnStart = false;
    int m_busyDepth = 0;
    bool m_inUserOperation = false;
    int m_lastNotifiedUpdates = -1;
};

#endif
