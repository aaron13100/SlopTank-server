// SlopTank modification notice: added or changed by SlopTank on 2026-11-12, 2026-11-14.
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
                var descendants = window.entireContents();
                for (var child = 0; child < descendants.length; child++) {
                    var descendant = descendants[child];
                    if (descendant.exists() && descendant.role() === "AXButton"
                            && descendant.name() === title && descendant.enabled()) return descendant;
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

    function captureChooserFailure(original, phase) {
        var record = { pid: cfg.pid, bundle: cfg.bundle, selectedTarget: cfg.selectedRoot,
            phase: phase, originalError: { message: String(original), code: nativeErrorCode(original) },
            windows: [], limits: { maxDepth: 5, maxNodes: 160, milliseconds: 5000 } };
        function save() {
            var text = $.NSString.alloc.initWithUTF8String(JSON.stringify(record, null, 2));
            if (!text.writeToFileAtomicallyEncodingError(cfg.diagnosticsPath, true, $.NSUTF8StringEncoding, null)) {
                welcomeError("diagnostics_write", "Could not retain chooser diagnostics at " + cfg.diagnosticsPath,
                    "Inspect the owned smoke work directory permissions.");
            }
        }
        function value(element, property) {
            try {
                var observed = element[property]();
                return observed === null || ["string", "number", "boolean"].indexOf(typeof observed) !== -1
                    ? observed : String(observed);
            } catch (error) {
                return { error: String(error), code: nativeErrorCode(error) };
            }
        }
        var nodes = 0;
        var until = Date.now() + record.limits.milliseconds;
        function hierarchy(element, depth) {
            if (depth > record.limits.maxDepth || nodes >= record.limits.maxNodes || Date.now() > until) {
                return { truncated: true };
            }
            nodes++;
            var entry = {};
            ["role", "name", "description", "enabled", "value"].forEach(function (property) {
                entry[property] = value(element, property);
            });
            try {
                var children = element.uiElements();
                entry.children = [];
                for (var index = 0; index < children.length && nodes < record.limits.maxNodes && Date.now() <= until; index++) {
                    entry.children.push(hierarchy(children[index], depth + 1));
                }
            } catch (error) {
                entry.childrenError = { message: String(error), code: nativeErrorCode(error) };
            }
            return entry;
        }
        try {
            save(); // Retain the original error even if an AX query later stalls.
            var windows = process.windows;
            for (var index = 0; index < windows.length && index < 4 && Date.now() <= until; index++) {
                var window = windows[index];
                var observed = { name: value(window, "name"), topLevelButtons: [], descendantButtons: [], sheets: [] };
                record.windows.push(observed);
                try {
                    var buttons = window.buttons;
                    for (var button = 0; button < buttons.length && button < 30; button++) {
                        observed.topLevelButtons.push({ name: value(buttons[button], "name"),
                            enabled: value(buttons[button], "enabled") });
                    }
                    var descendants = window.entireContents();
                    for (var child = 0; child < descendants.length && Date.now() <= until; child++) {
                        if (value(descendants[child], "role") === "AXButton") {
                            observed.descendantButtons.push({ name: value(descendants[child], "name"),
                                enabled: value(descendants[child], "enabled") });
                        }
                    }
                    save(); // Retain flat button observations before the bounded hierarchy.
                    var sheets = window.sheets;
                    for (var sheet = 0; sheet < sheets.length && sheet < 3; sheet++) {
                        observed.sheets.push(hierarchy(sheets[sheet], 0));
                    }
                } catch (error) {
                    observed.windowStateError = { message: String(error), code: nativeErrorCode(error) };
                }
                observed.hierarchy = hierarchy(window, 0);
                save();
            }
            record.nodesCaptured = nodes;
            save();
        } catch (error) {
            console.log("Chooser diagnostics failed without replacing original error: " + String(error));
        }
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
        var chooserPhase = "before-folder-panel";
        try {
            chooserPhase = "open-folder-panel";
            waitForButton(welcome, "Use another drive...", 10).click();
            var panel = waitForWindow("Choose Server Data Location", 20);
            chooserPhase = "go-to-folder";
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
            chooserPhase = "waiting-for-use-this-folder";
            waitForButton(panel, "Use This Folder", 15).click();
            chooserPhase = "waiting-for-selected-root-display";
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
        } catch (error) {
            captureChooserFailure(error, chooserPhase);
            throw error;
        }
    }
    waitForButton(welcome, "Start SlopTank", 10).click();
}
