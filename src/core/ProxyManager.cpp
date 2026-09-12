#include "ProxyManager.h"

#include <KConfigGroup>
#include <KSharedConfig>
#include <QNetworkAccessManager>
#include <QNetworkProxy>
#include <QProcessEnvironment>

ProxyManager::ProxyManager(QObject *parent)
    : QObject(parent)
{
}

void ProxyManager::load()
{
    const KConfigGroup group =
        KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))->group(QStringLiteral("Proxy"));
    m_mode = static_cast<Mode>(group.readEntry("Mode", int(Mode::None)));
    m_host = group.readEntry("Host", QString());
    m_port = quint16(group.readEntry("Port", 1080));
}

void ProxyManager::save() const
{
    KConfigGroup group =
        KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))->group(QStringLiteral("Proxy"));
    group.writeEntry("Mode", int(m_mode));
    group.writeEntry("Host", m_host);
    group.writeEntry("Port", int(m_port));
    group.sync();
}

ProxyManager::Mode ProxyManager::mode() const
{
    return m_mode;
}

void ProxyManager::setMode(Mode mode)
{
    m_mode = mode;
}

QString ProxyManager::host() const
{
    return m_host;
}

void ProxyManager::setHost(const QString &host)
{
    m_host = host;
}

quint16 ProxyManager::port() const
{
    return m_port;
}

void ProxyManager::setPort(quint16 port)
{
    m_port = port;
}

QString ProxyManager::makeUrl(Mode mode, const QString &host, quint16 port)
{
    if (mode == Mode::None || host.isEmpty() || port == 0)
        return QString();
    const QString scheme = mode == Mode::Socks5 ? QStringLiteral("socks5") : QStringLiteral("http");
    return QStringLiteral("%1://%2:%3").arg(scheme, host, QString::number(port));
}

QString ProxyManager::url() const
{
    return makeUrl(m_mode, m_host, m_port);
}

void ProxyManager::applyToNetworkAccessManager(QNetworkAccessManager *nam) const
{
    QNetworkProxy proxy;
    switch (m_mode) {
    case Mode::None:
        proxy.setType(QNetworkProxy::NoProxy);
        break;
    case Mode::Http:
        proxy.setType(QNetworkProxy::HttpProxy);
        proxy.setHostName(m_host);
        proxy.setPort(m_port);
        break;
    case Mode::Socks5:
        proxy.setType(QNetworkProxy::Socks5Proxy);
        proxy.setHostName(m_host);
        proxy.setPort(m_port);
        break;
    }
    nam->setProxy(proxy);
}
