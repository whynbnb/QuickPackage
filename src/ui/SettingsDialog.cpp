#include "SettingsDialog.h"

#include <KColorSchemeManager>
#include <KConfigGroup>
#include <KLocalizedString>
#include <KSharedConfig>
#include <QCheckBox>
#include <QComboBox>
#include <QDialogButtonBox>
#include <QFormLayout>
#include <QLabel>
#include <QPushButton>
#include <QVBoxLayout>

SettingsDialog::SettingsDialog(KColorSchemeManager *schemes, QWidget *parent)
    : QDialog(parent)
    , m_schemes(schemes)
{
    setWindowTitle(i18n("Settings"));
    setModal(true);

    auto *layout = new QVBoxLayout(this);
    auto *form = new QFormLayout;
    form->setContentsMargins(0, 0, 0, 0);

    m_languageCombo = new QComboBox(this);
    m_languageCombo->addItem(i18n("System"), QStringLiteral("system"));
    m_languageCombo->addItem(i18n("English"), QStringLiteral("en"));
    m_languageCombo->addItem(QStringLiteral("简体中文"), QStringLiteral("zh_CN"));
    form->addRow(i18n("Language:"), m_languageCombo);

    m_themeCombo = new QComboBox(this);
    m_themeCombo->addItem(i18n("System"), QStringLiteral("system"));
    m_themeCombo->addItem(i18n("Light"), QStringLiteral("light"));
    m_themeCombo->addItem(i18n("Dark"), QStringLiteral("dark"));
    form->addRow(i18n("Theme:"), m_themeCombo);

    layout->addLayout(form);

    m_startUpdatesCheck = new QCheckBox(i18n("Check for updates on startup"), this);
    layout->addWidget(m_startUpdatesCheck);

    auto *note = new QLabel(i18n("Language changes apply after restart."), this);
    note->setWordWrap(true);
    layout->addWidget(note);

    auto *buttonBox = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel,
                                           this);
    connect(buttonBox, &QDialogButtonBox::accepted, this, &SettingsDialog::apply);
    connect(buttonBox, &QDialogButtonBox::rejected, this, &QDialog::reject);
    layout->addWidget(buttonBox);

    KConfigGroup group = KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
                             ->group(QStringLiteral("General"));
    const QString language = group.readEntry(QStringLiteral("Language"), QStringLiteral("system"));
    m_languageCombo->setCurrentIndex(m_languageCombo->findData(language));
    const QString theme = group.readEntry(QStringLiteral("Theme"), QStringLiteral("system"));
    m_themeCombo->setCurrentIndex(m_themeCombo->findData(theme));
    m_startUpdatesCheck->setChecked(group.readEntry("CheckUpdatesOnStart", true));
}

void SettingsDialog::apply()
{
    KConfigGroup group = KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
                             ->group(QStringLiteral("General"));
    group.writeEntry("CheckUpdatesOnStart", m_startUpdatesCheck->isChecked());

    const QString language = m_languageCombo->currentData().toString();
    group.writeEntry("Language", language);

    const QString theme = m_themeCombo->currentData().toString();
    group.writeEntry("Theme", theme);
    group.sync();

    // Color scheme applies immediately
    if (m_schemes) {
        if (theme == QLatin1String("light"))
            m_schemes->activateSchemeId(QStringLiteral("BreezeLight"));
        else if (theme == QLatin1String("dark"))
            m_schemes->activateSchemeId(QStringLiteral("BreezeDark"));
        else
            m_schemes->activateSchemeId(QString());
    }

    accept();
}
