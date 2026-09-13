#ifndef QUICKPACKAGE_DETAILPANEL_H
#define QUICKPACKAGE_DETAILPANEL_H

#include "PackageModel.h"

#include <QHash>
#include <QProcessEnvironment>
#include <QString>
#include <QWidget>

class QFormLayout;
class QLabel;
class QNetworkAccessManager;
class QPushButton;

class DetailPanel : public QWidget
{
    Q_OBJECT

public:
    explicit DetailPanel(QWidget *parent = nullptr);

    void setPackage(const PackageModel::Package &package);
    void setProxyEnvironment(const QProcessEnvironment &env);
    void clear();

signals:
    void updateRequested(const PackageModel::Package &package);
    void uninstallRequested(const PackageModel::Package &package);
    void installRequested(const PackageModel::Package &package);

private:
    void fetchCargoDescription(const QString &name);
    void applyNetworkProxy();

    PackageModel::Package m_package;
    QProcessEnvironment m_netEnv;
    QNetworkAccessManager *m_nam = nullptr;
    QHash<QString, QString> m_descCache;
    QString m_fetchingName;

    QLabel *m_placeholder = nullptr;
    QWidget *m_content = nullptr;
    QFormLayout *m_formLayout = nullptr;
    QLabel *m_nameLabel = nullptr;
    QLabel *m_tagLabel = nullptr;
    QLabel *m_idLabel = nullptr;
    QLabel *m_versionLabel = nullptr;
    QLabel *m_originLabel = nullptr;
    QLabel *m_sizeLabel = nullptr;
    QLabel *m_dateLabel = nullptr;
    QLabel *m_reasonLabel = nullptr;
    QLabel *m_packagerLabel = nullptr;
    QLabel *m_urlLabel = nullptr;
    QLabel *m_licensesLabel = nullptr;
    QLabel *m_dependsLabel = nullptr;
    QLabel *m_binariesLabel = nullptr;
    QLabel *m_statusLabel = nullptr;
    QLabel *m_descLabel = nullptr;
    QPushButton *m_installButton = nullptr;
    QPushButton *m_updateButton = nullptr;
    QPushButton *m_uninstallButton = nullptr;
};

#endif
