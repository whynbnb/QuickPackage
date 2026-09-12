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

private:
    void apply();

    KColorSchemeManager *m_schemes = nullptr;
    QCheckBox *m_startUpdatesCheck = nullptr;
    QComboBox *m_languageCombo = nullptr;
    QComboBox *m_themeCombo = nullptr;
};

#endif
