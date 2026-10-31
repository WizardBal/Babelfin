'use strict';
// Public documentation contracts; standard libraries only, with no server or credentials.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const root = path.resolve(__dirname, '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8').replace(/\r\n/g, '\n');
const readme = read('README.md');
const contributing = read('CONTRIBUTING.md');
const notice = read('NOTICE');
const changelog = read('CHANGELOG.md');
const release = JSON.parse(read('release-config.json'));
const workflow = read('.github/workflows/verify.yml');
const native = read('tools/native-policy-check/README.md');

function section(document, title) {
    const heading = '## ' + title + '\n';
    assert.equal(document.split(heading).length, 2, 'Expected exactly one ' + title + ' section');
    return document.split(heading)[1].split('\n## ')[0];
}

test('README and CONTRIBUTING share one complete AI disclosure and bounded verification claims', () => {
    const disclosures = document => document.split(/\n\s*\n/).filter(p => /^AI tools are used for development\b/.test(p));
    const a = disclosures(readme), b = disclosures(contributing);
    assert.equal(a.length, 1, 'README needs one AI disclosure');
    assert.equal(b.length, 1, 'CONTRIBUTING needs the same disclosure');
    assert.equal(a[0], b[0]);
    assert.equal(a[0], disclosures(notice)[0]);
    assert.match(a[0], /AI tools are used for development, review and all translations/);
    assert.match(a[0], /Automated integration tests use Jellyfin Web on a disposable Jellyfin 12\.2 test instance/);
    assert.match(a[0], /Tested with Jellyfin 12\.4 and the Jellyfin Android app on Google Pixel 11 Pro XL/);
    assert.match(a[0], /Android TV, Swiftfin, Streamyfin and physical TVs have not been tested/);
    assert.equal((readme.match(/\ball translations\b/g) || []).length, 1, 'Do not repeat the disclosure near development instructions');
    for (const document of [readme, contributing]) assert.doesNotMatch(document, /Much of the code|This project was primarily developed|\*\*AI-developed project:/);
});

test('public prose avoids observer and maintainer narrative', () => {
    const files = ['README.md', 'CONTRIBUTING.md', 'NOTICE', 'CHANGELOG.md', 'SECURITY.md',
        'docs/API.md', 'docs/TESTING-COVERAGE.md', 'tools/native-policy-check/README.md',
        'tools/tests/ui-smoke/README.md', '.github/ISSUE_TEMPLATE/bug_report.yml',
        '.github/ISSUE_TEMPLATE/feature_request.yml', '.github/ISSUE_TEMPLATE/config.yml',
        'release-config.json', 'tools/package.py'];
    const observerVoice = /the owner reports|owner-reported|without reported issues|(?:app|Babelfin|server) versions? (?:were|was) not recorded|app version, player mode and Babelfin version were not recorded|WizardBal maintains the project|verified by (?:the )?owner|\bI (?:use|build|maintain|haven.t tested)\b/i;
    for (const file of files) assert.doesNotMatch(read(file), observerVoice, file);
});

test('first public beta notes do not prescribe an upgrade from private releases', () => {
    assert.match(changelog, /^## \S+ — First public beta$/m);
    assert.match(release.changelog, /^First public beta[.:]/);
    for (const notes of [changelog, release.changelog]) {
        assert.doesNotMatch(notes, /Existing installations|previous (?:beta|release)|upgrade from|phase[ -]?b|\bM4\b|0\.14\.0\.0/i);
        assert.match(notes, /Android TV, Swiftfin, Streamyfin and physical TVs have not been tested/);
        assert.match(notes, /disposable Jellyfin 12\.2 test instance/);
        assert.match(notes, /Tested with Jellyfin 12\.4 and the Jellyfin Android app on Google Pixel 11 Pro XL/);
    }
    assert.match(readme, /New catalog installations allow automatic plugin updates/);
    assert.match(readme, /Manual installation/);
});

test('API documentation belongs to development, with screenshot content kept together', () => {
    const screenshots = section(readme, 'Screenshots');
    assert.equal((screenshots.match(/!\[/g) || []).length, 3);
    assert.doesNotMatch(screenshots, /docs\/API\.md/);
    assert.match(section(readme, 'Development and testing'), /\[HTTP API\]\(docs\/API\.md\)/);
});

test('GPLv3 license and immutable upstream notices accompany the project copyright', () => {
    const notice = section(readme, 'License');
    assert.match(notice, /Copyright \(c\) 2026 WizardBal\./);
    assert.match(notice, /`GPL-3\.0-only`/);
    for (const file of ['LICENSE', 'NOTICE']) assert.ok(notice.includes('[' + file + '](' + file + ')'));
    assert.match(notice, /use, modification, forks and commercial use/);
    assert.match(notice, /corresponding source/);
    assert.match(notice, /No paid commercial license is required/);
    assert.match(notice, /do not claim legal clearance/);
    const license = read('LICENSE');
    assert.match(license, /^\s*GNU GENERAL PUBLIC LICENSE\n\s+Version 3, 29 June 2007/);
    assert.match(license, /Copyright \(C\) 2007 Free Software Foundation/);
    assert.match(license, /Everyone is permitted to copy and distribute verbatim copies/);
    assert.doesNotMatch(license, /Copyright \(c\) 2026 WizardBal/);
    const upstream = read('NOTICE');
    assert.match(upstream, /Jellyfin Project contributors/);
    assert.match(upstream, /blob\/[0-9a-f]{40}\/Emby\.Server\.Implementations\/Library\/MediaStreamSelector\.cs/);
    assert.match(upstream, /iso6392\.txt/);
    assert.match(upstream, /Local modifications/);
    assert.match(upstream, /GPL-3\.0-only/);
});

test('required installation-method field allows catalog, manual and uncertain reports', () => {
    const form = read('.github/ISSUE_TEMPLATE/bug_report.yml');
    const fields = form.split(/^  - type: /m).slice(1);
    const methods = fields.filter(field => /^\s+id: installation_method$/m.test(field));
    assert.equal(methods.length, 1, 'Missing or duplicate installation-method field');
    assert.match(methods[0], /^dropdown\n/);
    assert.match(methods[0], /^\s+required: true$/m);
    for (const option of ['Plugin catalog', 'Manual file copy', 'Other / not sure']) {
        assert.ok(methods[0].includes('        - ' + option + '\n'), 'Missing installation option: ' + option);
    }
    const ids = fields.flatMap(field => [...field.matchAll(/^\s+id: (\S+)$/gm)].map(match => match[1]));
    assert.equal(new Set(ids).size, ids.length, 'GitHub form field IDs must be unique');
    for (const id of ['jellyfin_version', 'babelfin_version', 'app_version', 'player_mode', 'sanitized_logs']) {
        const field = fields.find(field => field.includes('    id: ' + id + '\n'));
        assert.ok(field, 'Keep the ' + id + ' field');
        assert.match(field, /^\s+required: true$/m);
    }
    assert.match(form, /Do not upload your full Jellyfin configuration or Babelfin settings export/);
});

test('benchmark instructions keep measurement scope without machine details or recorded results', () => {
    const machineDetails = /\b(?:AMD|Intel|Ryzen)\b|\bbuild\s+\d{4,}\b|\b\d+\s+(?:cores|logical processors)\b|\.NET\s+\d+\.\d+\.\d+/i;
    assert.doesNotMatch(native, machineDetails, 'Public instructions must not disclose recorded host details');
    assert.doesNotMatch(native, /^## Recorded performance$/m);
    assert.doesNotMatch(native, /^\|[^|\n]+\|(?:\s*[\d,.]+\s*\|){2,}/m, 'Do not publish the old timing/allocation table');
    // Catch model/build/core/runtime variants without rejecting software prerequisites or oracle identity.
    for (const detail of ['AMD Sample Processor', 'Intel Sample Processor', 'Windows build 12345', 'build 12345', '8 cores', '12 logical processors', '.NET 1.2.3']) assert.match(detail, machineDetails);
    for (const prerequisite of ['.NET 10 SDK', 'Jellyfin 12.2 runtime', 'Emby.Server.Implementations 12.2.0.0']) assert.doesNotMatch(prerequisite, machineDetails);
    const performance = section(native, 'Benchmark measurements');
    assert.equal((performance.match(/--benchmark-only/g) || []).length, 2, 'Document both Release benchmark invocations');
    assert.equal((performance.match(/--configuration Release/g) || []).length, 2);
    assert.match(performance, /\$env:DOTNET_TieredCompilation = '0'/);
    assert.match(performance, /finally\s*\{/);
    assert.match(performance, /Remove-Item Env:DOTNET_TieredCompilation/);
    assert.match(performance, /\$env:DOTNET_TieredCompilation = \$previousTiering/);
    assert.match(performance, /15 tracks/);
    assert.match(performance, /at least 500 ms.*seven samples/);
    assert.match(performance, /20,000 decisions.*5,000/);
    assert.match(performance, /medians of sample means, not individual-request percentiles/);
    assert.match(performance, /excludes resolver cache misses, server\/network I\/O and full playback workload/);
    assert.match(performance, /not a production playback latency guarantee/);
    assert.match(performance, /Keep raw output in private artifacts and remove machine details before sharing/);
});

test('both CI platforms gate feature regressions with pinned actions and read-only permissions', () => {
    assert.match(workflow, /os: \[ubuntu-latest, windows-latest\]/);
    const sharedSteps = workflow.split(/^      - name: /m).slice(1).filter(step => step.startsWith('Auth and UI regressions\n'));
    assert.equal(sharedSteps.length, 1, 'Expected one shared Auth and UI step');
    const commands = [...sharedSteps[0].matchAll(/^\s+run: (node --test .+)$/gm)].map(match => match[1].split(/\s+/).slice(2));
    assert.equal(commands.length, 1, 'Expected one shared Node gate for both matrix platforms');
    for (const testFile of ['admin-regression', 'docs', 'auth-regression', 'browser-driver', 'preference-ui', 'release-ui', 'web-lifecycle', 'web-regression']) {
        assert.ok(commands[0].includes('tools/tests/' + testFile + '.test.js'), 'Keep gate: ' + testFile);
    }
    const actions = [...workflow.matchAll(/^\s+- uses: (\S+)/gm)].map(match => match[1]);
    assert.ok(actions.length >= 4);
    for (const action of actions) assert.match(action, /^actions\/[\w-]+@[a-f0-9]{40}$/);
    assert.match(workflow, /^permissions:\n  contents: read\n/m);
    assert.doesNotMatch(workflow, /(?:^\s*permissions:\s*(?:write-all|\{[^\n]*:\s*write\b)|^\s*[\w-]+:\s*write\b)/m);
    assert.doesNotMatch(workflow, /\bpull_request_target\b/);
    assert.match(workflow, /persist-credentials: false/);
});

test('owned documentation comments contain no internal phase or private-version labels', () => {
    for (const file of ['README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'tools/native-policy-check/README.md']) {
        for (const match of read(file).matchAll(/<!--[\s\S]*?-->/g)) {
            assert.doesNotMatch(match[0], /phase[ _-]?b|\bM4\b|private[ -]?version|\b0\.(?:[0-9]|1[0-4])\.0\.0\b/i, file);
        }
    }
});

test('activity is session memory and legacy activity files remain untouched', () => {
    const history = section(readme, 'Activity history and retained data');
    assert.match(history, /newest \*\*5,000\*\* activity entries in memory, newest first/);
    assert.match(history, /empties when Jellyfin restarts/);
    assert.match(history, /creates no activity files when recording or viewing/);
    assert.match(history, /Deleting an account removes that person's entries/);
    for (const name of ['babelfin/activity.jsonl', 'babelfin/activity-recovery/', 'babelfin/deleted-users/']) assert.ok(history.includes(name));
    assert.match(history, /Babelfin does not read or write legacy/);
    assert.match(history, /Existing files are left untouched and can be removed manually/);
    assert.match(history, /person settings file with a `\.deleted-\*` suffix/);
    assert.match(history, /Repaired-file backups/);
    assert.match(history, /copy backups/);
    assert.doesNotMatch(history, /16 MiB|64 MiB|quarantin|retention|compaction|back.off/i);
});

test('missing-item scheduled reports preserve settings and retained recovery exports', () => {
    const setup = section(readme, 'Administrator setup');
    assert.match(setup, /\*\*Babelfin: report missing-item overrides\*\*/);
    assert.match(setup, /\*\*Dashboard → Scheduled Tasks\*\*/);
    assert.match(setup, /runs on a daily interval and does not run at startup/);
    assert.match(setup, /run it manually from that page/);
    assert.match(setup, /reports references to missing items without removing overrides or burned-in subtitle flags/);
    assert.match(setup, /scheduled task and manual report endpoints leave saved settings and recovery files unchanged/);
    assert.match(setup, /skips active library scans and an empty or unavailable library/);
    assert.doesNotMatch(setup, /removes overrides.*after saving a per-person recovery export/);
    const history = section(readme, 'Activity history and retained data');
    const recovery = history.split(/\n\s*\n/).find(paragraph => paragraph.includes('`babelfin/pruned-overrides/`'));
    assert.ok(recovery, 'Document the private recovery exports in the activity and data section');
    assert.match(recovery, /can contain account names and IDs, preferences and references to deleted titles/);
    assert.match(recovery, /do not expire automatically/);
    assert.match(recovery, /Preview a recovery export/);
    assert.match(recovery, /report-only task does not create or remove these files/);
    assert.match(section(readme, 'Updates, disabling and removal'), /The entire `babelfin` directory/);
});

test('partial export and reset reports identify accounts and reasons without promising a full backup', () => {
    const setup = section(readme, 'Administrator setup');
    assert.match(setup, /Download all settings\*\* exports readable settings and reports any skipped accounts by name and reason/);
    assert.match(setup, /An export with skipped accounts is incomplete/);
    const reset = setup.split(/\n\s*\n/).find(paragraph => paragraph.startsWith('**Reset all settings**'));
    assert.ok(reset, 'Explain the reset report and its recovery export');
    assert.match(reset, /report for skipped or failed accounts, their names and reasons, and whether server settings were reset/);
    assert.match(reset, /Skipped accounts are omitted from the backup and left unchanged/);
    assert.match(reset, /failed Jellyfin remember-flag restoration can be reported after that person's settings have already been reset/);
    assert.match(reset, /Do not treat a partial export or reset backup as a full backup/);
});

test('public support and contribution instructions use recorded scope and concrete evidence', () => {
    const support = section(readme, 'App support');
    assert.match(support, /Automated integration tests use Jellyfin Web on a disposable Jellyfin 12\.2 test instance/);
    assert.match(support, /Android TV, Swiftfin, Streamyfin and physical TVs have not been tested/);
    assert.match(support, /Tested with Jellyfin 12\.4 and the Jellyfin Android app on Google Pixel 11 Pro XL/);
    assert.match(support, /does not evaluate a device's direct-play audio compatibility/);
    assert.match(support, /another automatic track selector[\s\S]*disabled first/);
    assert.match(support, /Without a device ID, choices made before playback are not remembered/);
    assert.doesNotMatch(support, /Some players accept server choices|Configuring settings in a browser does not establish support/);
    assert.match(support, /do not verify a physical remote, native decoder or native app/);
    assert.match(contributing, /For playback changes, include the app and version, player mode, expected and actual tracks, and results from a disposable Jellyfin server/);
    assert.doesNotMatch(contributing, /simulated services alone do not establish native-app support/);
});

test('native policy documentation explains repeatable checks without historical lab results', () => {
    assert.match(native, /^## Policy parity and normalization$/m);
    assert.match(native, /loaded assembly identity, MVID and SHA256/);
    assert.match(native, /source revision and runtime being evaluated/);
    assert.doesNotMatch(native, /\b\d{4}-\d{2}-\d{2}\b|^[- ]*MVID:|^[- ]*SHA256:|All [\d,]+ comparisons matched|^## Recorded oracle/m);
    assert.match(native, /all five subtitle modes/);
    assert.match(native, /do not measure application-wide test coverage/);
    for (const option of ['--parity-only', '--inspect-normalization', '--benchmark-only']) assert.ok(native.includes(option));
});

test('packaging and rig module instructions describe reusable workflows without personal publication assumptions', () => {
    const packageDoc = read('tools/package.py').split('"""')[1];
    const rigDoc = read('tools/rig/rig.py').split('"""')[1];
    assert.match(packageDoc, /Check repository and release-asset access before sharing their URLs/);
    assert.doesNotMatch(packageDoc, /A private repository/);
    assert.match(rigDoc, /disposable Jellyfin 12\.2 Windows instance/);
    assert.doesNotMatch(rigDoc, new RegExp(['on this ' + 'PC', 'Every build ' + 'passes this'].join('|')));
    assert.match(rigDoc, /--jellyfin/);
});

test('migration test names describe preserved behavior and remain uniquely discoverable', () => {
    const source = read('plugin/tests/Babelfin.Engine.Tests/Regression/GenreMigrationTests.cs');
    const methods = [...source.matchAll(/public void (\w+)\(\)/g)].map(match => match[1]);
    assert.deepEqual(methods, [
        'DirectLegacyOrderSurvivesClassification_AndSplitArchivesStateTheirAmbiguity',
        'LegacyForeignTitlesMaterialisesAudioAtGenrePrecedence_WithoutChangingArchives',
        'DisablingPlaybackDoesNotEraseSavedPreferencesStatus',
        'AdminStatusIgnoresDeletedLibraryRows_InLogicAndEndpoint'
    ]);
    assert.equal(new Set(methods).size, 4);
    assert.equal((source.match(/\[Fact\]/g) || []).length, 4);
});

test('native policy source comments retain attribution and point to reproducible verification', () => {
    const header = read('plugin/src/Jellyfin.Plugin.Babelfin/Engine/Selection/VideoSubtitleSelector.cs').split('\n\n')[0];
    assert.match(header, /Policy verification: tools\/native-policy-check\/NativePolicyCheck\.csproj/);
    assert.match(header, /https:\/\/github\.com\/jellyfin\/jellyfin\/blob\/(?:master|[0-9a-f]{40})\/Emby\.Server\.Implementations\/Library\/MediaStreamSelector\.cs/);
    assert.match(header, /Jellyfin Project contributors/);
    assert.match(read('NOTICE'), /Adapted subtitle policy/);
    assert.match(read('LICENSE'), /Version 3, 29 June 2007/);
    assert.doesNotMatch(header, /Oracle binary SHA256:|Policy oracle: \.rig\//);
});

test('package fallback and catalog share a plain first-public-beta feature summary', () => {
    const declaration = /DEFAULT_CHANGELOG = \(([\s\S]*?)\n\)/.exec(read('tools/package.py'));
    assert.ok(declaration, 'The package fallback must be a visible literal summary');
    const fallback = [...declaration[1].matchAll(/"(?:\\.|[^"\\])*"/g)].map(match => JSON.parse(match[0])).join('');
    assert.equal(fallback, release.changelog);
    assert.match(fallback, /^First public beta[.:]/);
    assert.match(fallback, /library and genre/);
    assert.match(fallback, /Android TV, Swiftfin, Streamyfin and physical TVs have not been tested/);
    assert.doesNotMatch(fallback, /\b(?:fixed|safer|previous release|internal|checkpoint)\b/i);
});

test('README links the measured coverage report without inventing percentages', () => {
    assert.match(section(readme, 'Development and testing'), /\[testing and coverage\]\(docs\/TESTING-COVERAGE\.md\)/);
    const report = read('docs/TESTING-COVERAGE.md');
    assert.match(report, /^# Testing Babelfin/m);
    assert.match(report, /Line and branch coverage measure code reached by a particular run/);
    assert.doesNotMatch(readme, /\b\d+(?:\.\d+)?%\s+(?:line|branch|method|function|test|coverage)/i);
});

test('problem reports use Jellyfin logs and title-check output', () => {
    const help = section(readme, 'Troubleshooting');
    assert.match(help, /Dashboard → Logs/);
    assert.match(help, /lines containing `\[Babelfin\]`/);
    assert.match(help, /Check a title/);
    const history = section(readme, 'Activity history and retained data');
    assert.match(history, /Per-title compatibility writes are Debug messages/);
    assert.match(history, /undo summary is logged at Information/);
});

test('manual installation verifies downloads and keeps versioned plugin folders', () => {
    assert.match(readme, /### Manual installation/);
    assert.match(readme, /sha256sum -c Babelfin-/);
    assert.match(readme, /shasum -a 256 -c Babelfin-/);
    assert.match(readme, /Get-FileHash/);
    assert.match(readme, /Dashboard → Paths → Data/);
    assert.match(readme, /config\/plugins/);
    assert.match(readme, /config\/data\/plugins/);
    assert.match(readme, /568:568/);
    assert.match(readme, /ZIP's five files/);
    assert.doesNotMatch(readme, /install-babelfin|Manual container installation/);
});

test('Linux release package and upload preserve seven verified files', () => {
    const steps = workflow.split(/^      - name: /m).slice(1);
    const build = steps.find(step => step.startsWith('Build the release package (Linux)\n'));
    const verify = steps.find(step => step.startsWith('Verify release bytes\n'));
    const upload = steps.find(step => step.startsWith('Upload Linux release package\n'));
    assert.ok(build && verify && upload);
    assert.match(build, /python tools\/package\.py/);
    assert.match(build, /python tools\/source_archive\.py/);
    assert.match(build, /--tag v0\.19\.0-beta\.1/);
    assert.match(build, /cp NOTICE/);
    assert.match(build, /sha256sum -c Babelfin-0\.19\.0\.0\.zip\.sha256 Babelfin-0\.19\.0\.0-source\.zip\.sha256/);
    assert.match(build, /sha256sum --binary/);
    assert.match(verify, /if current != recorded:/);
    assert.match(verify, /len\(rows\) != len\(files\)/);
    const paths = upload.split('          path: |\n')[1].split(/\n(?=\S| {0,10}\S)/)[0].trim().split('\n').map(line => line.trim());
    assert.deepEqual(paths, ['Babelfin-0.19.0.0.zip', 'Babelfin-0.19.0.0.zip.md5', 'Babelfin-0.19.0.0.zip.sha256', 'manifest.json', 'Babelfin-0.19.0.0-source.zip', 'Babelfin-0.19.0.0-source.zip.sha256', 'NOTICE'].map(name => '${{ runner.temp }}/babelfin-real-package/' + name));
    assert.match(upload, /steps\.verify_release_bytes\.outcome == 'success'/);
    assert.doesNotMatch(workflow, /install-babelfin|integration_installer_docker|test_plugins_inventory/);
});
