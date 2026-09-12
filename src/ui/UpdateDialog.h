#ifndef QUICKPACKAGE_UPDATEDIALOG_H
#define QUICKPACKAGE_UPDATEDIALOG_H

#include "PackageModel.h"

#include <QDialog>

class QTreeWidget;

class UpdateDialog : public QDialog
{
    Q_OBJECT

public:
    explicit UpdateDialog(const QVector<PackageModel::Package> &updates,
                          QWidget *parent = nullptr);

    QVector<PackageModel::Package> checkedPackages() const;

private:
    QVector<PackageModel::Package> m_updates;
    QTreeWidget *m_tree = nullptr;
};

#endif
