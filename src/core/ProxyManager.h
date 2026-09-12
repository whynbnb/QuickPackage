#ifndef QUICKPACKAGE_PROXYMANAGER_H
#define QUICKPACKAGE_PROXYMANAGER_H

#include <QObject>
#include <QString>

class QProcessEnvironment;
class QNetworkAccessManager;

class ProxyManager : public QObject
{
    Q_OBJECT

public:
    enum class Mode { None = 0, Http, Socks5 };
    Q_ENUM(Mode)

    explicit ProxyManager(QObject *parent = nullptr);

    void load();
    void save() const;

    Mode mode() const;
    void setMode(Mode mode);
    QString host() const;
    void setHost(const QString &host);
    quint16 port() const;
    void setPort(quint16 port);
    QString url() const;

    // Returns http://host:port or socks5://host:port depending on mode.
    static QString makeUrl(Mode mode, const QString &host, quint16 port);

    void applyToNetworkAccessManager(QNetworkAccessManager *nam) const;

signals:
    void changed();

private:
    Mode m_mode = Mode::None;
    QString m_host;
    quint16 m_port = 1080;
};

#endif
