#ifndef QUICKPACKAGE_PACKAGECARDDELEGATE_H
#define QUICKPACKAGE_PACKAGECARDDELEGATE_H

#include <QStyledItemDelegate>

class PackageCardDelegate : public QStyledItemDelegate
{
    Q_OBJECT

public:
    explicit PackageCardDelegate(QObject *parent = nullptr);

    void paint(QPainter *painter, const QStyleOptionViewItem &option,
               const QModelIndex &index) const override;
    QSize sizeHint(const QStyleOptionViewItem &option,
                   const QModelIndex &index) const override;

protected:
    bool editorEvent(QEvent *event, QAbstractItemModel *model,
                     const QStyleOptionViewItem &option,
                     const QModelIndex &index) override;

signals:
    void selectionToggled(const QModelIndex &index, bool checked) const;

private:
    QRect checkBoxRect(const QStyleOptionViewItem &option) const;
};

#endif
