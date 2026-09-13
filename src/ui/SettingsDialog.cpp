#include "SettingsDialog.h"

#include <KColorSchemeManager>
#include <KConfigGroup>
#include <KLocalizedString>
#include <KSharedConfig>
#include <QCheckBox>
#include <QComboBox>
#include <QDialogButtonBox>
#include <QFormLayout>
#include <QGroupBox>
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

    auto *backendGroup = new QGroupBox(i18n("Package backends"), this);
    auto *backendLayout = new QVBoxLayout(backendGroup);
    m_pacmanCheck = new QCheckBox(i18n("Enable pacman"), backendGroup);
    backendLayout->addWidget(m_pacmanCheck);
    m_flatpakCheck = new QCheckBox(i18n("Enable Flatpak"), backendGroup);
    backendLayout->addWidget(m_flatpakCheck);
    m_cargoCheck = new QCheckBox(i18n("Enable Cargo"), backendGroup);
    backendLayout->addWidget(m_cargoCheck);
    layout->addWidget(backendGroup);

    // Developer tool managers are optional and disabled by default
    auto *devGroup = new QGroupBox(i18n("Developer tools"), this);
    auto *devLayout = new QVBoxLayout(devGroup);
    m_miseCheck = new QCheckBox(i18n("Enable mise"), devGroup);
    devLayout->addWidget(m_miseCheck);
    m_rustupCheck = new QCheckBox(i18n("Enable rustup"), devGroup);
    devLayout->addWidget(m_rustupCheck);
    m_fvmCheck = new QCheckBox(i18n("Enable fvm"), devGroup);
    devLayout->addWidget(m_fvmCheck);
    layout->addWidget(devGroup);

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

    m_pacmanEnabled = group.readEntry("EnablePacman", true);
    m_pacmanCheck->setChecked(m_pacmanEnabled);
    m_flatpakEnabled = group.readEntry("EnableFlatpak", true);
    m_flatpakCheck->setChecked(m_flatpakEnabled);
    m_cargoEnabled = group.readEntry("EnableCargo", true);
    m_cargoCheck->setChecked(m_cargoEnabled);
    // Developer tool backends are optional and disabled by default.
    m_miseEnabled = group.readEntry("EnableMise", false);
    m_miseCheck->setChecked(m_miseEnabled);
    m_rustupEnabled = group.readEntry("EnableRustup", false);
    m_rustupCheck->setChecked(m_rustupEnabled);
    m_fvmEnabled = group.readEntry("EnableFvm", false);
    m_fvmCheck->setChecked(m_fvmEnabled);
}

void SettingsDialog::apply()
{
    KConfigGroup group = KSharedConfig::openConfig(QStringLiteral("quickpackagerc"))
                             ->group(QStringLiteral("General"));
    group.writeEntry("CheckUpdatesOnStart", m_startUpdatesCheck->isChecked());

    const bool pacmanEnabled = m_pacmanCheck->isChecked();
    const bool flatpakEnabled = m_flatpakCheck->isChecked();
    const bool cargoEnabled = m_cargoCheck->isChecked();
    const bool miseEnabled = m_miseCheck->isChecked();
    const bool rustupEnabled = m_rustupCheck->isChecked();
    const bool fvmEnabled = m_fvmCheck->isChecked();
    const bool backendChanged = pacmanEnabled != m_pacmanEnabled
        || flatpakEnabled != m_flatpakEnabled || cargoEnabled != m_cargoEnabled
        || miseEnabled != m_miseEnabled || rustupEnabled != m_rustupEnabled
        || fvmEnabled != m_fvmEnabled;
    m_pacmanEnabled = pacmanEnabled;
    m_flatpakEnabled = flatpakEnabled;
    m_cargoEnabled = cargoEnabled;
    m_miseEnabled = miseEnabled;
    m_rustupEnabled = rustupEnabled;
    m_fvmEnabled = fvmEnabled;
    group.writeEntry("EnablePacman", pacmanEnabled);
    group.writeEntry("EnableFlatpak", flatpakEnabled);
    group.writeEntry("EnableCargo", cargoEnabled);
    group.writeEntry("EnableMise", miseEnabled);
    group.writeEntry("EnableRustup", rustupEnabled);
    group.writeEntry("EnableFvm", fvmEnabled);

    const QString language = m_languageCombo->currentData().toString();
    group.writeEntry("Language", language);

    const QString theme = m_themeCombo->currentData().toString();
    group.writeEntry("Theme", theme);
    group.sync();

    if (backendChanged)
        Q_EMIT backendsChanged();

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
