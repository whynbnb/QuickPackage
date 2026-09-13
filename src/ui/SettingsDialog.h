#ifndef QUICKPACKAGE_SETTINGSDIALOG_H
#define QUICKPACKAGE_SETTINGSDIALOG_H

#include <QDialog>

class QCheckBox;
class QComboBox;

class KColorSchemeManager;

class SettingsDialog : public QDialog
{
    Q_OBJECT

public:
    explicit SettingsDialog(KColorSchemeManager *schemes,
                            QWidget *parent = nullptr);

signals:
    // Emitted when the enabled package backends changed and were saved.
    void backendsChanged();

private:
    void apply();

    KColorSchemeManager *m_schemes = nullptr;
    QCheckBox *m_startUpdatesCheck = nullptr;
    QCheckBox *m_pacmanCheck = nullptr;
    QCheckBox *m_flatpakCheck = nullptr;
    QCheckBox *m_cargoCheck = nullptr;
    QCheckBox *m_miseCheck = nullptr;
    QCheckBox *m_rustupCheck = nullptr;
    QCheckBox *m_fvmCheck = nullptr;
    QComboBox *m_languageCombo = nullptr;
    QComboBox *m_themeCombo = nullptr;
    bool m_pacmanEnabled = true;
    bool m_flatpakEnabled = true;
    bool m_cargoEnabled = true;
    // Developer tool backends are optional and disabled by default.
    bool m_miseEnabled = false;
    bool m_rustupEnabled = false;
    bool m_fvmEnabled = false;
};

#endif
