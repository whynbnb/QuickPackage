#include "ui/MainWindow.h"

#include <KAboutData>
#include <KLocalizedString>
#include <QApplication>
#include <QCommandLineParser>
#include <QIcon>

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    KLocalizedString::setApplicationDomain("quickpackage");

    KAboutData aboutData(
        QStringLiteral("quickpackage"), i18n("QuickPackage"),
        QStringLiteral("0.3.0"), i18n("KDE-style package manager for pacman, Flatpak and cargo"),
        KAboutLicense::GPL_V3, i18n("© 2026 QuickPackage contributors"));
    aboutData.addAuthor(i18n("QuickPackage contributors"));
    aboutData.setDesktopFileName(QStringLiteral("org.quickpackage.QuickPackage"));
    KAboutData::setApplicationData(aboutData);

    app.setDesktopFileName(QStringLiteral("org.quickpackage.QuickPackage"));
    app.setWindowIcon(QIcon::fromTheme(QStringLiteral("quickpackage"),
                                       QIcon::fromTheme(QStringLiteral("package-x-generic"))));

    QCommandLineParser parser;
    aboutData.setupCommandLine(&parser);
    parser.process(app);
    aboutData.processCommandLine(&parser);

    // KMainWindow sets WA_DeleteOnClose, so the window must live on the heap
    auto *window = new MainWindow;
    window->show();

    return app.exec();
}
