'use strict';
// API error-code contract used by both translation validators.
module.exports = Object.freeze([
    'invalid-settings', 'library-unavailable', 'storage-unavailable', 'newer-format',
    'account-recreated', 'account-archived', 'damaged-settings', 'settings-changed',
    'burned-in-conflict', 'backup-failed', 'import-blocked', 'import-not-applied',
    'not-running', 'forbidden', 'field-conflict', 'item-unavailable', 'person-unavailable',
    'missing-settings', 'copy-target-required', 'simulation-target-required',
    'simulation-busy', 'empty-import', 'invalid-import', 'invalid-override-scope',
    'invalid-override-target', 'invalid-collection-target', 'burned-in-limit', 'override-limit'
]);
