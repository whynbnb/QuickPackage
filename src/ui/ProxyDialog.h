#ifndef QUICKPACKAGE_PROXYDIALOG_H
#define QUICKPACKAGE_PROXYDIALOG_H

#include "ProxyManager.h"

#include <QDialog>

class QComboBox;
class QSpinBox;
class QLineEdit;
class QLabel;

class ProxyDialog : public QDialog
{
    Q_OBJECT

public:
    explicit ProxyDialog(ProxyManager *proxy, QWidget *parent = nullptr);

private:
    void updateEnabledState();
    void testConnection();
    void apply();

    ProxyManager *m_proxy = nullptr;
    QComboBox *m_modeCombo = nullptr;
    QLineEdit *m_hostEdit = nullptr;
    QSpinBox *m_portSpin = nullptr;
    QLabel *m_testResult = nullptr;
    QPushButton *m_testButton = nullptr;
};

#endif
