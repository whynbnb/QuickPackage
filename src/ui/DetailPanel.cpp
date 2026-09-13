#include "DetailPanel.h"

#include <KLocalizedString>
#include <QColor>
#include <QFont>
#include <QFormLayout>
#include <QHBoxLayout>
#include <QIcon>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLabel>
#include <QNetworkAccessManager>
#include <QNetworkProxy>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QPushButton>
#include <QUrl>
#include <QVBoxLayout>

using Package = PackageModel::Package;
using Backend = PackageModel::Backend;
using Status = PackageModel::Status;

namespace {

QString backendName(Backend backend)
{
    switch (backend) {
    case Backend::Pacman:
        return i18n("Pacman");
    case Backend::Flatpak:
        return i18n("Flatpak");
    case Backend::Cargo:
        return i18n("Cargo");
    case Backend::Mise:
        return i18n("Mise");
    case Backend::Rustup:
        return i18n("Rustup");
    case Backend::Fvm:
        return i18n("FVM");
    }
    return {};
}

QColor backendColor(Backend backend)
{
    switch (backend) {
    case Backend::Pacman:
        return QColor(56, 121, 216);
    case Backend::Flatpak:
        return QColor(64, 160, 140);
    case Backend::Cargo:
        return QColor(207, 90, 33);
    case Backend::Mise:
        return QColor(190, 145, 0);
    case Backend::Rustup:
        return QColor(190, 70, 110);
    case Backend::Fvm:
        return QColor(100, 115, 165);
    }
    return {};
}

void setRowVisible(QFormLayout *form, QLabel *field, bool on)
{
    form->setRowVisible(static_cast<QWidget *>(field), on);
}

} // namespace

DetailPanel::DetailPanel(QWidget *parent)
    : QWidget(parent)
    , m_nam(new QNetworkAccessManager(this))
{
    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);

    m_placeholder = new QLabel(i18n("Select a package to see details"), this);
    m_placeholder->setAlignment(Qt::AlignCenter);
    m_placeholder->setWordWrap(true);
    layout->addWidget(m_placeholder, 1);

    m_content = new QWidget(this);
    auto *contentLayout = new QVBoxLayout(m_content);
    contentLayout->setContentsMargins(4, 4, 4, 4);
    contentLayout->setSpacing(8);

    auto *headerRow = new QHBoxLayout;
    headerRow->setSpacing(8);
    m_nameLabel = new QLabel(m_content);
    m_nameLabel->setWordWrap(true);
    m_nameLabel->setTextInteractionFlags(Qt::TextSelectableByMouse);
    QFont nameFont = m_nameLabel->font();
    nameFont.setBold(true);
    nameFont.setPointSizeF(nameFont.pointSizeF() * 1.3);
    m_nameLabel->setFont(nameFont);
    headerRow->addWidget(m_nameLabel, 1);

    m_tagLabel = new QLabel(m_content);
    m_tagLabel->setAlignment(Qt::AlignCenter);
    headerRow->addWidget(m_tagLabel);
    contentLayout->addLayout(headerRow);

    m_statusLabel = new QLabel(m_content);
    contentLayout->addWidget(m_statusLabel);

    m_formLayout = new QFormLayout;
    m_formLayout->setLabelAlignment(Qt::AlignLeft | Qt::AlignTop);
    const auto addField = [this](QLabel **field, const QString &labelText) {
        *field = new QLabel(m_content);
        (*field)->setWordWrap(true);
        (*field)->setTextInteractionFlags(Qt::TextSelectableByMouse);
        m_formLayout->addRow(labelText, *field);
    };
    addField(&m_idLabel, i18n("Id:"));
    addField(&m_versionLabel, i18n("Version:"));
    addField(&m_originLabel, i18n("Origin:"));
    addField(&m_sizeLabel, i18n("Size:"));
    addField(&m_dateLabel, i18n("Install date:"));
    addField(&m_reasonLabel, i18n("Install reason:"));
    addField(&m_packagerLabel, i18n("Packager:"));
    addField(&m_urlLabel, i18n("URL:"));
    addField(&m_licensesLabel, i18n("Licenses:"));
    addField(&m_dependsLabel, i18n("Depends on:"));
    addField(&m_binariesLabel, i18n("Binaries:"));
    contentLayout->addLayout(m_formLayout);

    m_descLabel = new QLabel(m_content);
    m_descLabel->setWordWrap(true);
    m_descLabel->setTextInteractionFlags(Qt::TextSelectableByMouse);
    contentLayout->addWidget(m_descLabel);

    contentLayout->addStretch();

    auto *buttonRow = new QHBoxLayout;
    buttonRow->setSpacing(6);
    m_installButton = new QPushButton(i18n("Install"), m_content);
    m_installButton->setIcon(QIcon::fromTheme(QStringLiteral("list-add")));
    buttonRow->addWidget(m_installButton);
    m_updateButton = new QPushButton(i18n("Update"), m_content);
    m_updateButton->setIcon(QIcon::fromTheme(QStringLiteral("system-software-update"),
                                             QIcon::fromTheme(QStringLiteral("view-refresh"))));
    buttonRow->addWidget(m_updateButton);
    m_uninstallButton = new QPushButton(i18n("Uninstall"), m_content);
    m_uninstallButton->setIcon(QIcon::fromTheme(QStringLiteral("list-remove")));
    buttonRow->addWidget(m_uninstallButton);
    contentLayout->addLayout(buttonRow);

    layout->addWidget(m_content, 1);
    m_content->hide();

    connect(m_installButton, &QPushButton::clicked, this,
            [this] { Q_EMIT installRequested(m_package); });
    connect(m_updateButton, &QPushButton::clicked, this,
            [this] { Q_EMIT updateRequested(m_package); });
    connect(m_uninstallButton, &QPushButton::clicked, this,
            [this] { Q_EMIT uninstallRequested(m_package); });
}

void DetailPanel::setProxyEnvironment(const QProcessEnvironment &env)
{
    m_netEnv = env;
    applyNetworkProxy();
}

void DetailPanel::applyNetworkProxy()
{
    const QString url = m_netEnv.value(QStringLiteral("all_proxy"),
                          m_netEnv.value(QStringLiteral("ALL_PROXY"),
                          m_netEnv.value(QStringLiteral("https_proxy"),
                          m_netEnv.value(QStringLiteral("http_proxy")))));
    if (url.isEmpty()) {
        m_nam->setProxy(QNetworkProxy::NoProxy);
        return;
    }
    QNetworkProxy proxy;
    if (url.startsWith(QLatin1String("socks5://")))
        proxy.setType(QNetworkProxy::Socks5Proxy);
    else
        proxy.setType(QNetworkProxy::HttpProxy);
    const QUrl parsed(url);
    proxy.setHostName(parsed.host());
    proxy.setPort(parsed.port(url.startsWith(QLatin1String("socks5://")) ? 1080 : 8080));
    proxy.setUser(parsed.userName());
    proxy.setPassword(parsed.password());
    m_nam->setProxy(proxy);
}

void DetailPanel::fetchCargoDescription(const QString &name)
{
    if (m_descCache.contains(name)) {
        const QString cached = m_descCache.value(name);
        m_descLabel->setText(cached);
        m_descLabel->setVisible(!cached.isEmpty());
        return;
    }
    if (m_fetchingName == name)
        return;
    m_fetchingName = name;
    m_descLabel->setText(i18n("Loading description…"));
    m_descLabel->setVisible(true);
    QNetworkRequest request(QUrl(QStringLiteral("https://crates.io/api/v1/crates/") + name));
    request.setHeader(QNetworkRequest::UserAgentHeader,
                      QStringLiteral("QuickPackage/1.0 (package manager)"));
    QNetworkReply *reply = m_nam->get(request);
    connect(reply, &QNetworkReply::finished, this, [this, reply, name] {
        reply->deleteLater();
        if (m_fetchingName != name)
            return;
        m_fetchingName.clear();
        QString description;
        if (reply->error() == QNetworkReply::NoError) {
            const QJsonDocument doc = QJsonDocument::fromJson(reply->readAll());
            description = doc.object().value(QLatin1String("crate"))
                              .toObject().value(QLatin1String("description")).toString();
        }
        m_descCache.insert(name, description);
        if (m_package.name == name && m_package.backend == Backend::Cargo) {
            m_descLabel->setText(description);
            m_descLabel->setVisible(!description.isEmpty());
        }
    });
}

void DetailPanel::setPackage(const Package &pkg)
{
    m_package = pkg;
    m_nameLabel->setText(pkg.name);

    const QString id = pkg.packageId.isEmpty() ? pkg.name : pkg.packageId;
    m_idLabel->setText(id);
    setRowVisible(m_formLayout, m_idLabel, !id.isEmpty() && id != pkg.name);

    m_versionLabel->setText(pkg.newVersion.isEmpty()
                                ? pkg.version
                                : QStringLiteral("%1 → %2").arg(
                                    pkg.version.isEmpty() ? QStringLiteral("—") : pkg.version,
                                    pkg.newVersion));
    setRowVisible(m_formLayout, m_versionLabel, !m_versionLabel->text().isEmpty());

    m_originLabel->setText(pkg.origin);
    setRowVisible(m_formLayout, m_originLabel, !pkg.origin.isEmpty());

    m_sizeLabel->setText(pkg.size);
    setRowVisible(m_formLayout, m_sizeLabel, !pkg.size.isEmpty());

    const QUrl url(pkg.url);
    m_urlLabel->setText(pkg.url);
    setRowVisible(m_formLayout, m_urlLabel, !pkg.url.isEmpty());
    if (url.isValid() && !pkg.url.isEmpty()) {
        m_urlLabel->setText(QStringLiteral("<a href=\"%1\">%1</a>").arg(pkg.url));
        m_urlLabel->setTextFormat(Qt::RichText);
        m_urlLabel->setOpenExternalLinks(true);
    }

    m_dateLabel->setText(pkg.installDate);
    setRowVisible(m_formLayout, m_dateLabel, !pkg.installDate.isEmpty());

    m_reasonLabel->setText(pkg.installReason);
    setRowVisible(m_formLayout, m_reasonLabel, !pkg.installReason.isEmpty());

    m_packagerLabel->setText(pkg.packager);
    setRowVisible(m_formLayout, m_packagerLabel, !pkg.packager.isEmpty());

    m_licensesLabel->setText(pkg.licenses);
    setRowVisible(m_formLayout, m_licensesLabel, !pkg.licenses.isEmpty());

    m_dependsLabel->setText(pkg.depends);
    setRowVisible(m_formLayout, m_dependsLabel, !pkg.depends.isEmpty());

    m_binariesLabel->setText(pkg.binaries);
    setRowVisible(m_formLayout, m_binariesLabel, !pkg.binaries.isEmpty());

    const QString status = PackageModel::statusText(pkg.status);
    m_statusLabel->setText(status);
    m_statusLabel->setVisible(!status.isEmpty());

    m_descLabel->setTextFormat(Qt::PlainText);
    m_descLabel->setOpenExternalLinks(false);
    m_descLabel->setText(pkg.description);
    m_descLabel->setVisible(!pkg.description.isEmpty());
    if (pkg.backend == Backend::Cargo && pkg.description.isEmpty())
        fetchCargoDescription(pkg.name);

    const QColor color = backendColor(pkg.backend);
    m_tagLabel->setText(backendName(pkg.backend));
    m_tagLabel->setStyleSheet(
        QStringLiteral("QLabel { color: %1; background: rgba(%2, %3, %4, 40);"
                       " border-radius: 9px; padding: 2px 10px; }")
            .arg(color.name(), QString::number(color.red()), QString::number(color.green()),
                 QString::number(color.blue())));
    m_tagLabel->setVisible(!m_tagLabel->text().isEmpty());

    m_updateButton->setEnabled(pkg.status == Status::NeedsUpdate);
    m_uninstallButton->setEnabled(pkg.installed);
    m_installButton->setEnabled(!pkg.installed);

    m_placeholder->hide();
    m_content->show();
}

void DetailPanel::clear()
{
    m_package = Package{};
    m_content->hide();
    m_placeholder->show();
}
