#include "PackageCardDelegate.h"

#include "core/PackageModel.h"

#include <KLocalizedString>
#include <QApplication>
#include <QEvent>
#include <QIcon>
#include <QModelIndex>
#include <QMouseEvent>
#include <QPainter>
#include <QPainterPath>
#include <QStyle>
#include <QStyleOptionButton>

PackageCardDelegate::PackageCardDelegate(QObject *parent)
    : QStyledItemDelegate(parent)
{
}

QRect PackageCardDelegate::checkBoxRect(const QStyleOptionViewItem &option) const
{
    QStyleOptionButton opt;
    opt.rect = option.rect;
    opt.state = option.state;
    const QSize size = option.widget->style()->subElementRect(QStyle::SE_CheckBoxIndicator,
                                                             &opt, option.widget).size();
    QRect r(0, 0, size.width(), size.height());
    r.moveLeft(option.rect.left() + 12);
    r.moveTop(option.rect.top() + (option.rect.height() - r.height()) / 2);
    return r;
}

void PackageCardDelegate::paint(QPainter *painter, const QStyleOptionViewItem &option,
                                const QModelIndex &index) const
{
    const QString name = index.data(PackageModel::NameRole).toString();
    const QString version = index.data(PackageModel::VersionRole).toString();
    const QString newVersion = index.data(PackageModel::NewVersionRole).toString();
    const QString description = index.data(PackageModel::DescriptionRole).toString();
    const auto backend = static_cast<PackageModel::Backend>(
        index.data(PackageModel::BackendRole).toInt());
    const auto status = static_cast<PackageModel::Status>(
        index.data(PackageModel::StatusRole).toInt());
    const bool selected = index.data(PackageModel::IsSelectedRole).toBool();

    painter->save();
    painter->setRenderHint(QPainter::Antialiasing, true);

    const QRect card = option.rect.adjusted(6, 3, -6, -3);
    QPainterPath path;
    path.addRoundedRect(card, 6, 6);

    const QPalette &pal = option.palette;
    QColor bg = pal.base().color();
    if (option.state & QStyle::State_MouseOver)
        bg = pal.alternateBase().color();
    if (selected)
        bg = pal.highlight().color();
    painter->fillPath(path, bg);
    if (selected) {
        QPen pen(pal.highlight().color().darker(130), 1);
        painter->setPen(pen);
        painter->drawPath(path);
    } else {
        QPen pen(pal.mid().color(), 1, Qt::DotLine);
        painter->setPen(pen);
        painter->drawPath(path);
    }

    // Checkbox
    {
        QStyleOptionButton opt;
        opt.rect = checkBoxRect(option);
        opt.state = QStyle::State_Enabled;
        if (selected)
            opt.state |= QStyle::State_On;
        else
            opt.state |= QStyle::State_Off;
        if (option.state & QStyle::State_MouseOver)
            opt.state |= QStyle::State_MouseOver;
        option.widget->style()->drawControl(QStyle::CE_CheckBox, &opt, painter, option.widget);
    }

    // Icon
    QString iconName;
    switch (backend) {
    case PackageModel::Backend::Pacman:
        iconName = QStringLiteral("applications-system");
        break;
    case PackageModel::Backend::Flatpak:
        iconName = QStringLiteral("flatpak");
        break;
    case PackageModel::Backend::Cargo:
        iconName = QStringLiteral("rust");
        break;
    }
    const QIcon icon = QIcon::fromTheme(iconName,
                                        QIcon::fromTheme(QStringLiteral("package-x-generic")));
    const QRect iconRect(card.left() + 44, card.top() + (card.height() - 32) / 2, 32, 32);
    icon.paint(painter, iconRect, Qt::AlignCenter);

    // Text area
    const int textLeft = iconRect.right() + 10;
    const int textRight = card.right() - 12;
    const QRect textArea(textLeft, card.top() + 6, textRight - textLeft, card.height() - 12);

    QFont nameFont = option.font;
    nameFont.setBold(true);
    QFontMetrics fmName(nameFont);
    QFontMetrics fm(option.font);

    // Badge on the right
    QString badge;
    QColor badgeColor;
    switch (status) {
    case PackageModel::Status::NeedsUpdate:
        badge = i18n("Update");
        badgeColor = QColor(246, 116, 0);
        break;
    case PackageModel::Status::Installed:
        badge = i18n("Installed");
        badgeColor = pal.highlight().color();
        break;
    case PackageModel::Status::Updating:
        badge = i18n("Updating…");
        badgeColor = QColor(0, 120, 210);
        break;
    case PackageModel::Status::Removing:
        badge = i18n("Removing…");
        badgeColor = QColor(0, 120, 210);
        break;
    case PackageModel::Status::Failed:
        badge = i18n("Failed");
        badgeColor = QColor(200, 40, 40);
        break;
    case PackageModel::Status::Idle:
        break;
    }

    int badgeWidth = 0;
    if (!badge.isEmpty()) {
        QFont badgeFont = option.font;
        badgeFont.setPointSizeF(option.font.pointSizeF() * 0.85);
        const QFontMetrics fmBadge(badgeFont);
        badgeWidth = fmBadge.horizontalAdvance(badge) + 16;
        QRect badgeRect(textRight - badgeWidth, textArea.top(), badgeWidth, 20);
        badgeRect.moveTop(textArea.top() + 2);

        QColor bg = badgeColor;
        bg.setAlpha(selected ? 220 : 45);
        QColor fg;
        if (selected)
            fg = pal.highlightedText().color();
        else
            fg = badgeColor.darker(140);

        QPainterPath badgePath;
        badgePath.addRoundedRect(badgeRect, 10, 10);
        painter->fillPath(badgePath, bg);
        painter->setFont(badgeFont);
        painter->setPen(fg);
        painter->drawText(badgeRect, Qt::AlignCenter, badge);
    }

    // Name + version row
    painter->setFont(nameFont);
    painter->setPen(selected ? pal.highlightedText().color() : pal.text().color());
    const int nameAvail = textArea.width() - badgeWidth - 8;
    const QString elidedName = fmName.elidedText(name, Qt::ElideRight, nameAvail * 3 / 5);
    const int nameWidth = fmName.horizontalAdvance(elidedName);
    painter->drawText(QRect(textArea.left(), textArea.top(), nameAvail * 3 / 5,
                            fmName.height()),
                      Qt::AlignVCenter | Qt::AlignLeft, elidedName);

    painter->setFont(option.font);
    QColor verColor = selected ? pal.highlightedText().color() : pal.mid().color();
    painter->setPen(verColor);
    QString versionText = version.isEmpty() ? QString() : version;
    QRect verRect(textArea.left() + nameWidth + 10, textArea.top(),
                  textArea.right() - (textArea.left() + nameWidth + 10), fmName.height());
    if (!newVersion.isEmpty()) {
        const QString arrow = QStringLiteral(" → %1").arg(newVersion);
        const QString verElided = fm.elidedText(versionText + arrow, Qt::ElideRight,
                                                verRect.width());
        painter->drawText(verRect, Qt::AlignVCenter | Qt::AlignLeft, verElided);
    } else if (!versionText.isEmpty()) {
        painter->drawText(verRect, Qt::AlignVCenter | Qt::AlignLeft,
                          fm.elidedText(versionText, Qt::ElideRight, verRect.width()));
    }

    // Description row
    if (!description.isEmpty()) {
        painter->setPen(selected ? pal.highlightedText().color()
                                 : pal.placeholderText().color());
        const QString desc = fm.elidedText(description, Qt::ElideRight, textArea.width());
        painter->drawText(QRect(textArea.left(), textArea.bottom() - fm.height() + 1,
                                textArea.width(), fm.height()),
                          Qt::AlignVCenter | Qt::AlignLeft, desc);
    }

    painter->restore();
}

QSize PackageCardDelegate::sizeHint(const QStyleOptionViewItem &option,
                                    const QModelIndex &index) const
{
    Q_UNUSED(index);
    const QFontMetrics fm(option.font);
    return {360, fm.height() * 2 + 30};
}

bool PackageCardDelegate::editorEvent(QEvent *event, QAbstractItemModel *model,
                                      const QStyleOptionViewItem &option,
                                      const QModelIndex &index)
{
    Q_UNUSED(model);
    if (event->type() == QEvent::MouseButtonRelease
        && checkBoxRect(option).contains(static_cast<QMouseEvent *>(event)->pos())) {
        const bool checked = index.data(PackageModel::IsSelectedRole).toBool();
        Q_EMIT selectionToggled(index, !checked);
        return true;
    }
    return QStyledItemDelegate::editorEvent(event, model, option, index);
}
