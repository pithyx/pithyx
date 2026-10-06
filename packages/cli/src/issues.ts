/** One problem with an app package: an error code of the box with its params, as the daemon reports it. */
export interface Issue {
  code:
    | 'app.manifest.invalid_json'
    | 'app.manifest.schema_violation'
    | 'app.manifest.version_unsupported'
    | 'app.manifest.id_invalid'
    | 'app.manifest.id_reserved'
    | 'app.manifest.sdk_invalid'
    | 'app.manifest.window_size_invalid'
    | 'app.manifest.duplicate'
    | 'app.manifest.reference_unknown'
    | 'app.manifest.cron_invalid'
    | 'app.compose.yaml_invalid'
    | 'app.compose.key_unsupported'
    | 'app.compose.value_invalid'
    | 'app.compose.reference_unknown'
    | 'app.compose.storage_mismatch'
    | 'app.compose.right_missing'
    | 'app.compose.right_unused'
    | 'app.elevated.unsupported'
    | 'app.icon.invalid'
    | 'app.package.layout_invalid';
  params: Record<string, string | number>;
}
