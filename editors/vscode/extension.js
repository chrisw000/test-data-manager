// TDM feature-file support — thin LSP client (W4-D3).
// All the intelligence lives in `tdm lsp` inside the dotnet tool; this extension only
// launches it, scopes it to the workspace's configured featurePaths (so it coexists with
// Reqnroll/Cucumber extensions), and surfaces model-staleness (the risk-table mitigation).
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

function workspaceRoot() {
    const folder = (vscode.workspace.workspaceFolders || [])[0];
    return folder ? folder.uri.fsPath : undefined;
}

/** featurePaths from tdm.settings.json, as workspace-relative glob patterns. */
function featureGlobs(root) {
    try {
        const settings = JSON.parse(fs.readFileSync(path.join(root, 'tdm.settings.json'), 'utf8'));
        const paths = (settings.run && settings.run.featurePaths) || [];
        const globs = paths
            .map(p => String(p).replace(/\\/g, '/').replace(/^\.\//, ''))
            .filter(p => p.length > 0);
        if (globs.length > 0) return globs;
    } catch {
        // Malformed settings — fall through to the default.
    }
    return ['features/**/*.feature'];
}

function sha256(filePath) {
    return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

/** Warns when tdm.model.json was exported from a different tdm.settings.json (stale model). */
function checkModelStaleness(root, modelPath) {
    try {
        const settingsFile = path.join(root, 'tdm.settings.json');
        if (!fs.existsSync(modelPath) || !fs.existsSync(settingsFile)) return;
        const model = JSON.parse(fs.readFileSync(modelPath, 'utf8'));
        if (!model.settingsFileSha256 || model.settingsFileSha256 === sha256(settingsFile)) return;

        const regenerate = 'Regenerate';
        vscode.window
            .showWarningMessage(
                'tdm.model.json is stale — it was exported from a different tdm.settings.json. ' +
                'Entity/property validation may be wrong.', regenerate)
            .then(choice => {
                if (choice !== regenerate) return;
                const toolPath = vscode.workspace.getConfiguration('tdm').get('toolPath', 'tdm');
                const terminal = vscode.window.createTerminal({ name: 'tdm export-model', cwd: root });
                terminal.show();
                terminal.sendText(`${toolPath} export-model --settings tdm.settings.json --out ${path.basename(modelPath)}`);
            });
    } catch {
        // Unreadable model — the server itself reports missing/broken models.
    }
}

function activate(context) {
    const root = workspaceRoot();
    if (!root || !fs.existsSync(path.join(root, 'tdm.settings.json'))) return;

    const config = vscode.workspace.getConfiguration('tdm');
    const toolPath = config.get('toolPath', 'tdm');
    const modelPath = path.join(root, config.get('modelPath', 'tdm.model.json'));

    // Claim only files under the configured featurePaths — Reqnroll/Cucumber extensions
    // keep the rest.
    const documentSelector = featureGlobs(root).map(glob => ({
        scheme: 'file',
        pattern: new vscode.RelativePattern(root, glob),
    }));

    client = new LanguageClient(
        'tdm',
        'TDM Language Server',
        {
            command: toolPath,
            args: ['lsp', '--model', modelPath],
            options: { cwd: root },
        },
        { documentSelector },
    );
    context.subscriptions.push(client);
    client.start().catch(err =>
        vscode.window.showErrorMessage(
            `Failed to start "${toolPath} lsp" — is the TDM dotnet tool installed? (${err.message})`));

    checkModelStaleness(root, modelPath);
    const watcher = vscode.workspace.createFileSystemWatcher(
        new vscode.RelativePattern(root, '{tdm.settings.json,' + path.basename(modelPath) + '}'));
    watcher.onDidChange(() => checkModelStaleness(root, modelPath));
    watcher.onDidCreate(() => checkModelStaleness(root, modelPath));
    context.subscriptions.push(watcher);
}

function deactivate() {
    return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
