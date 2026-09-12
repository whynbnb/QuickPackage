#ifndef QUICKPACKAGE_SETTINGSDIALOG_H
#define QUICKPACKAGE_SETTINGSDIALOG_H

#include <QDialog>

class QCheckBox;

class SettingsDialog : public QDialog
{
    Q_OBJECT

public:
    explicit SettingsDialog(QWidget *parent = nullptr);

private:
    QCheckBox *m_startUpdatesCheck = nullptr;
};

#endif
