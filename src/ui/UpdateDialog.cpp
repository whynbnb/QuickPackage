#include "UpdateDialog.h"

#include <KLocalizedString>
#include <QDialogButtonBox>
#include <QHeaderView>
#include <QPushButton>
#include <QTreeWidget>
#include <QVBoxLayout>

using Package = PackageModel::Package;

UpdateDialog::UpdateDialog(const QVector<Package> &updates, QWidget *parent)
    : QDialog(parent)
    , m_updates(updates)
{
    setWindowTitle(i18np("1 Update Available", "%1 Updates Available", updates.size()));
    resize(520, 400);

    auto *layout = new QVBoxLayout(this);

    m_tree = new QTreeWidget(this);
    m_tree->setRootIsDecorated(false);
    m_tree->setAllColumnsShowFocus(true);
    m_tree->setHeaderLabels({i18n("Package"), i18n("Backend"), i18n("Current"),
                             i18n("New")});
    m_tree->header()->setSectionResizeMode(QHeaderView::ResizeToContents);
    for (const Package &pkg : updates) {
        auto *item = new QTreeWidgetItem(m_tree);
        item->setText(0, pkg.name);
        item->setText(1, PackageModel::backendName(pkg.backend));
        item->setText(2, pkg.version);
        item->setText(3, pkg.newVersion);
        item->setCheckState(0, Qt::Checked);
    }
    layout->addWidget(m_tree);

    auto *buttonBox = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel,
                                           this);
    buttonBox->button(QDialogButtonBox::Ok)->setText(i18n("Update"));
    connect(buttonBox, &QDialogButtonBox::accepted, this, &QDialog::accept);
    connect(buttonBox, &QDialogButtonBox::rejected, this, &QDialog::reject);
    layout->addWidget(buttonBox);
}

QVector<Package> UpdateDialog::checkedPackages() const
{
    QVector<Package> result;
    for (int i = 0; i < m_tree->topLevelItemCount(); ++i) {
        const auto *item = m_tree->topLevelItem(i);
        if (item->checkState(0) == Qt::Checked) {
            for (const Package &pkg : m_updates) {
                if (pkg.name == item->text(0)) {
                    result.append(pkg);
                    break;
                }
            }
        }
    }
    return result;
}
