#include "SettingsDialog.h"

#include <KConfigGroup>
#include <KLocalizedString>
#include <KSharedConfig>
#include <QCheckBox>
#include <QDialogButtonBox>
#include <QVBoxLayout>

SettingsDialog::SettingsDialog(QWidget *parent)
    : QDialog(parent)
{
    setWindowTitle(i18n("Settings"));
    setModal(true);

    auto *layout = new QVBoxLayout(this);

    m_startUpdatesCheck = new QCheckBox(i18n("Check for updates on startup"), this);
    layout->addWidget(m_startUpdatesCheck);

    auto *buttonBox = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel,
                                           this);
    connect(buttonBox, &QDialogButtonBox::accepted, this, [this] {
        KConfigGroup group = KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
                                 ->group(QStringLiteral("General"));
        group.writeEntry("CheckUpdatesOnStart", m_startUpdatesCheck->isChecked());
        group.sync();
        accept();
    });
    connect(buttonBox, &QDialogButtonBox::rejected, this, &QDialog::reject);
    layout->addWidget(buttonBox);

    KConfigGroup group = KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
                             ->group(QStringLiteral("General"));
    m_startUpdatesCheck->setChecked(group.readEntry("CheckUpdatesOnStart", true));
}
