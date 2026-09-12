#include "ProxyDialog.h"

#include <KLocalizedString>
#include <QComboBox>
#include <QDialogButtonBox>
#include <QFormLayout>
#include <QLabel>
#include <QLineEdit>
#include <QNetworkAccessManager>
#include <QNetworkProxy>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QPushButton>
#include <QSpinBox>
#include <QVBoxLayout>

using ProxyMode = ProxyManager::Mode;

ProxyDialog::ProxyDialog(ProxyManager *proxy, QWidget *parent)
    : QDialog(parent)
    , m_proxy(proxy)
{
    setWindowTitle(i18n("Proxy Settings"));
    setModal(true);

    auto *layout = new QVBoxLayout(this);
    auto *form = new QFormLayout;
    form->setContentsMargins(0, 0, 0, 0);

    m_modeCombo = new QComboBox(this);
    m_modeCombo->addItem(i18n("No proxy"), int(ProxyMode::None));
    m_modeCombo->addItem(i18n("HTTP"), int(ProxyMode::Http));
    m_modeCombo->addItem(i18n("SOCKS5"), int(ProxyMode::Socks5));
    form->addRow(i18n("Mode:"), m_modeCombo);

    m_hostEdit = new QLineEdit(this);
    m_hostEdit->setPlaceholderText(QStringLiteral("127.0.0.1"));
    form->addRow(i18n("Host:"), m_hostEdit);

    m_portSpin = new QSpinBox(this);
    m_portSpin->setRange(1, 65535);
    m_portSpin->setValue(7890);
    form->addRow(i18n("Port:"), m_portSpin);

    layout->addLayout(form);

    m_testResult = new QLabel(this);
    m_testResult->setWordWrap(true);
    layout->addWidget(m_testResult);

    auto *buttonBox = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel,
                                           this);
    m_testButton = new QPushButton(i18n("Test"), buttonBox);
    buttonBox->addButton(m_testButton, QDialogButtonBox::ActionRole);
    connect(buttonBox, &QDialogButtonBox::accepted, this, &ProxyDialog::apply);
    connect(buttonBox, &QDialogButtonBox::rejected, this, &QDialog::reject);
    connect(m_testButton, &QPushButton::clicked, this, &ProxyDialog::testConnection);
    connect(m_modeCombo, &QComboBox::currentIndexChanged, this,
            [this](int index) { Q_UNUSED(index); updateEnabledState(); });

    layout->addWidget(buttonBox);

    const ProxyMode mode = m_proxy->mode();
    m_modeCombo->setCurrentIndex(m_modeCombo->findData(int(mode)));
    m_hostEdit->setText(m_proxy->host());
    if (m_proxy->port() > 0)
        m_portSpin->setValue(m_proxy->port());
    updateEnabledState();
}

void ProxyDialog::updateEnabledState()
{
    const bool enabled =
        m_modeCombo->currentData().toInt() != int(ProxyMode::None);
    m_hostEdit->setEnabled(enabled);
    m_portSpin->setEnabled(enabled);
    m_testButton->setEnabled(enabled);
}

void ProxyDialog::testConnection()
{
    const auto mode = static_cast<ProxyMode>(m_modeCombo->currentData().toInt());
    const QString url =
        ProxyManager::makeUrl(mode, m_hostEdit->text().trimmed(),
                              quint16(m_portSpin->value()));
    if (url.isEmpty()) {
        m_testResult->setText(i18n("Invalid proxy configuration."));
        return;
    }

    m_testButton->setEnabled(false);
    m_testResult->setText(i18n("Testing connection…"));

    auto *nam = new QNetworkAccessManager(this);
    QNetworkProxy proxy;
    if (mode == ProxyMode::Http)
        proxy.setType(QNetworkProxy::HttpProxy);
    else
        proxy.setType(QNetworkProxy::Socks5Proxy);
    proxy.setHostName(m_hostEdit->text().trimmed());
    proxy.setPort(quint16(m_portSpin->value()));
    nam->setProxy(proxy);

    QNetworkRequest request{QUrl(QStringLiteral("https://archlinux.org"))};
    request.setTransferTimeout(8000);
    auto *reply = nam->get(request);
    connect(reply, &QNetworkReply::finished, this, [this, reply, nam] {
        nam->deleteLater();
        reply->deleteLater();
        m_testButton->setEnabled(true);
        if (reply->error() == QNetworkReply::NoError) {
            m_testResult->setText(i18nc("@info:status", "Connection successful."));
        } else {
            m_testResult->setText(
                i18nc("@info:status", "Connection failed: %1", reply->errorString()));
        }
    });
}

void ProxyDialog::apply()
{
    const auto mode = static_cast<ProxyMode>(m_modeCombo->currentData().toInt());
    m_proxy->setMode(mode);
    m_proxy->setHost(m_hostEdit->text().trimmed());
    m_proxy->setPort(quint16(m_portSpin->value()));
    m_proxy->save();
    Q_EMIT m_proxy->changed();
    accept();
}
