cmake_minimum_required(VERSION 3.24)
project(QuickPackage VERSION 0.3.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

include(GNUInstallDirs)

find_package(Qt6 REQUIRED COMPONENTS Widgets Network Concurrent)
find_package(KF6CoreAddons REQUIRED)
find_package(KF6Config REQUIRED)
find_package(KF6I18n REQUIRED)
find_package(KF6Notifications REQUIRED)
find_package(KF6IconThemes REQUIRED)
find_package(KF6XmlGui REQUIRED)
find_package(KF6WidgetsAddons REQUIRED)
find_package(KF6ColorScheme REQUIRED)

include_directories(${CMAKE_CURRENT_SOURCE_DIR}/src ${CMAKE_CURRENT_SOURCE_DIR}/src/core)

set(QUICKPACKAGE_SOURCES
    src/main.cpp
    src/core/PackageModel.cpp
    src/core/PackageManager.cpp
    src/core/ProxyManager.cpp
    src/ui/DetailPanel.cpp
    src/ui/MainWindow.cpp
    src/ui/PackageCardDelegate.cpp
    src/ui/ProxyDialog.cpp
    src/ui/SettingsDialog.cpp
    src/ui/UpdateDialog.cpp
)

add_executable(quickpackage ${QUICKPACKAGE_SOURCES})

target_link_libraries(quickpackage PRIVATE
    Qt6::Widgets
    Qt6::Network
    Qt6::Concurrent
    KF6::CoreAddons
    KF6::ConfigCore
    KF6::I18n
    KF6::Notifications
    KF6::IconThemes
    KF6::XmlGui
    KF6::WidgetsAddons
    KF6::ColorScheme
)

# Compile the Chinese translation catalog
add_custom_command(OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/quickpackage.mo
    COMMAND msgfmt --check -o ${CMAKE_CURRENT_BINARY_DIR}/quickpackage.mo
            ${CMAKE_CURRENT_SOURCE_DIR}/translations/quickpackage.po
    DEPENDS translations/quickpackage.po
    COMMENT "Compiling zh_CN translation catalog"
)
add_custom_target(translations ALL DEPENDS ${CMAKE_CURRENT_BINARY_DIR}/quickpackage.mo)

target_compile_options(quickpackage PRIVATE -Wall -Wextra)

install(TARGETS quickpackage RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
install(FILES data/org.quickpackage.QuickPackage.desktop
        DESTINATION ${CMAKE_INSTALL_DATADIR}/applications)
install(FILES data/org.quickpackage.QuickPackage.metainfo.xml
        DESTINATION ${CMAKE_INSTALL_DATADIR}/metainfo)
install(FILES data/quickpackage.notifyrc
        DESTINATION ${CMAKE_INSTALL_DATADIR}/knotifications6)
install(FILES data/quickpackageui.rc
        DESTINATION ${CMAKE_INSTALL_DATADIR}/kxmlgui5/quickpackage)
install(FILES ${CMAKE_CURRENT_BINARY_DIR}/quickpackage.mo
        DESTINATION ${CMAKE_INSTALL_DATADIR}/locale/zh_CN/LC_MESSAGES)
