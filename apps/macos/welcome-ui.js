// SlopTank modification notice: added or changed by SlopTank on 2026-11-12.
/** PID-scoped native welcome-window actions used by the installed-app smoke. */
ObjC.import("AppKit");
ObjC.import("Foundation");

class WelcomeUIError extends Error {
    constructor(code, message, hint, cause) {
        super(message);
        this.name = "WelcomeUIError";
        this.code = code;
        this.hint = hint;
        if (cause) this.cause = cause;
    }
}

function welcomeError(code, message, hint, cause) {
    throw new WelcomeUIError(code, message, hint, cause);
}

function nativeErrorCode(error) {
    return error.number || error.code || "unknown";
}

function readConfig(configPath) {
    var raw = $.NSString.stringWithContentsOfFileEncodingError(
        configPath, $.NSUTF8StringEncoding, null);
    if (!raw) welcomeError("config_unreadable", "Could not read welcome UI config: " + configPath,
        "Check the config path and read permissions.");
    try {
        return JSON.parse(ObjC.unwrap(raw));
    } catch (error) {
        welcomeError("config_invalid", "Welcome UI config is not valid JSON: " + String(error),
            "Regenerate the config with the installed-app smoke driver.", error);
    }
}

function run(argv) {
    if (!argv || argv.length !== 1) {
        welcomeError("config_argument", "Expected one JSON config path argument.",
            "Invoke welcome-ui.js with the config path as its only argument.");
    }
    var cfg = readConfig(argv[0]);
    var app = $.NSRunningApplication.runningApplicationWithProcessIdentifier(cfg.pid);
    if (!app || ObjC.unwrap(app.isTerminated)) {
        welcomeError("app_missing", "Owned SlopTank PID is not running.",
            "Confirm the launcher process is still running.");
    }
    var actualBundle = ObjC.unwrap(app.bundleURL.path);
    if (actualBundle !== cfg.bundle) {
        welcomeError("bundle_mismatch", "PID bundle mismatch: " + actualBundle,
            "Use the recorded app PID and its copied bundle.");
    }
    var systemEvents = Application("System Events");
    var process = systemEvents.processes.whose({ unixId: cfg.pid })[0];
    if (!process || !process.exists()) {
        welcomeError("process_unavailable", "System Events cannot see the owned PID " + cfg.pid + ".",
            "Inspect the runner's accessibility automation permission.");
    }

    function delayBriefly() { delay(0.2); }
    function findWindow(title) {
        try {
            var windows = process.windows;
            for (var index = 0; index < windows.length; index++) {
                var candidate = windows[index];
                if (candidate.exists() && candidate.name() === title) return candidate;
            }
        } catch (error) {
            welcomeError("window_enumeration", "Could not enumerate or inspect windows for target PID " +
                cfg.pid + ": " + String(error) + " (code " + nativeErrorCode(error) + ")",
            "Inspect the target app's accessibility window list.", error);
        }
        return null;
    }
    function waitForWindow(title, seconds) {
        var until = Date.now() + seconds * 1000;
        while (Date.now() < until) {
            var candidate = findWindow(title);
            if (candidate) return candidate;
            delayBriefly();
        }
        welcomeError("window_timeout", "Timed out waiting for owned-app window: " + title,
            "Inspect the app log and the runner's accessibility state.");
    }
    function waitForButton(window, title, seconds) {
        var until = Date.now() + seconds * 1000;
        while (Date.now() < until) {
            try {
                var buttons = window.buttons;
                for (var index = 0; index < buttons.length; index++) {
                    var button = buttons[index];
                    if (button.exists() && button.name() === title && button.enabled()) return button;
                }
            } catch (error) {
                welcomeError("button_enumeration", "Could not enumerate or inspect buttons for target PID " +
                    cfg.pid + ": " + String(error) + " (code " + nativeErrorCode(error) + ")",
                "Inspect the target window's accessibility button list.", error);
            }
            delayBriefly();
        }
        welcomeError("button_timeout", "Timed out waiting for button: " + title,
            "Inspect the target window's accessibility buttons.");
    }

    var welcomeTitle = "Welcome to SlopTank";
    if (cfg.action === "assert-no-welcome") {
        var welcomeUntil = Date.now() + 2000;
        while (Date.now() < welcomeUntil) {
            if (findWindow(welcomeTitle)) {
                welcomeError("welcome_reappeared", "Welcome window reappeared after profile initialization.",
                    "Inspect the profile state and launcher startup path.");
            }
            delayBriefly();
        }
        return;
    }
    if (cfg.action !== "start-default" && cfg.action !== "choose-and-start") {
        welcomeError("unknown_action", "Unknown welcome action: " + cfg.action,
            "Use start-default, choose-and-start, or assert-no-welcome.");
    }
    process.frontmost = true;
    var welcome = waitForWindow(welcomeTitle, 30);
    if (cfg.action === "choose-and-start") {
        if (!cfg.selectedRoot) {
            welcomeError("root_missing", "Alternate data root was not supplied.",
                "Provide the selected data root to the welcome action.");
        }
        waitForButton(welcome, "Use another drive...", 10).click();
        var panel = waitForWindow("Choose Server Data Location", 20);
        systemEvents.keystroke("g", { using: ["command down", "shift down"] });
        var sheet = null;
        var sheetUntil = Date.now() + 10 * 1000;
        while (Date.now() < sheetUntil) {
            if (panel.sheets.length > 0) { sheet = panel.sheets[0]; break; }
            delayBriefly();
        }
        if (!sheet || !sheet.exists() || sheet.textFields.length === 0) {
            welcomeError("folder_sheet_missing", "Go to Folder sheet did not expose its path field.",
                "Inspect the folder chooser's accessibility fields.");
        }
        systemEvents.keystroke("a", { using: ["command down"] });
        systemEvents.keystroke(cfg.selectedRoot);
        systemEvents.keyCode(36);
        var navigationUntil = Date.now() + 15 * 1000;
        while (panel.sheets.length > 0 && Date.now() < navigationUntil) delayBriefly();
        if (panel.sheets.length > 0) {
            welcomeError("folder_navigation_timeout", "Go to Folder did not finish navigating to " + cfg.selectedRoot,
                "Inspect the chooser's path field and any navigation error.");
        }
        waitForButton(panel, "Use This Folder", 15).click();
        // The welcome remains present behind the modal. Its new location proves the selection completed.
        var locationUntil = Date.now() + 15 * 1000;
        var shown = [];
        var selectedRootDisplayed = false;
        while (Date.now() < locationUntil) {
            welcome = findWindow(welcomeTitle);
            if (welcome) {
                try {
                    shown = welcome.staticTexts().map(function (text) { return String(text.value()); });
                } catch (error) {
                    welcomeError("location_inspection", "Could not inspect the selected data location for PID " +
                        cfg.pid + ": " + String(error) + " (code " + nativeErrorCode(error) + ")",
                    "Inspect the welcome window's accessibility text.", error);
                }
                selectedRootDisplayed = shown.some(function (value) { return value.indexOf(cfg.selectedRoot) !== -1; });
                if (selectedRootDisplayed) break;
            }
            delayBriefly();
        }
        if (!selectedRootDisplayed) {
            welcomeError("root_not_displayed", "Welcome window does not display the selected data root " +
                cfg.selectedRoot + ": " + JSON.stringify(shown),
            "Confirm the folder chooser applied the selected path.");
        }
    }
    waitForButton(welcome, "Start SlopTank", 10).click();
}
