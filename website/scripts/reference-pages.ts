// The pages of the generated API reference under src/content/docs/docs/reference/sdk/ (scripts/generate-sdk-reference.ts).
// Each page lists the public exports it documents, by the package entry point they come from; the generator fails
// when an export of an entry point is on no page, so a new export needs a line here. Title, description and intro of
// each page are site strings in src/content/i18n/<locale>.json: reference.<package>.<id>.title|description|intro.

/** A public entry point of a package: the file its `exports` map points `pithyx-source` at. */
export interface EntryPoint {
  /** The import path apps use. */
  specifier: string;
  /** The source file, relative to the repository root. */
  file: string;
}

export interface ReferencePage {
  /** The file name under reference/sdk/ (sdk) or reference/sdk/ui/ (ui), without extension. */
  id: string;
  package: 'sdk' | 'ui';
  /** Exports documented here, per entry point specifier; `*` takes every export of that entry point. */
  exports: Record<string, string[] | '*'>;
}

export const entryPoints: EntryPoint[] = [
  { specifier: '@pithyx/sdk', file: 'sdk/src/index.ts' },
  { specifier: '@pithyx/sdk/host', file: 'sdk/src/host.ts' },
  { specifier: '@pithyx/sdk/i18n', file: 'sdk/src/i18n.ts' },
  { specifier: '@pithyx/ui', file: 'packages/ui/src/index.ts' },
  { specifier: '@pithyx/ui/native', file: 'packages/ui/src/native.ts' },
];

/** The tokens page (reference/sdk/ui/tokens) is generated from this file, not from TypeDoc. */
export const tokensFile = 'packages/ui/tokens.css';

export const pages: ReferencePage[] = [
  {
    id: 'app',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': [
        'connect',
        'createApp',
        'CreateAppOptions',
        'PithyxApp',
        'Methods',
        'MethodName',
        'createFormatters',
        'Formatters',
        'NotificationRequest',
        'NotificationAction',
        'NotificationActivation',
        'ContextMenuItem',
        'Point',
        'DirectoryUser',
        'DirectoryGroup',
        'LogLevel',
        'LogData',
        'LogRecord',
        'inProcessPair',
        'portEndpoint',
        'Endpoint',
        'AppMessage',
        'HostMessage',
        'ConnectMessage',
        'REQUEST_TIMEOUT',
        'UNTIMED_METHODS',
        'SDK_VERSION',
        'PROTOCOL_VERSION',
        'MIN_PROTOCOL_VERSION',
        'AppManifest',
        'AppId',
        'AppAuthor',
        'LocalizedName',
        'LocalizedText',
        'Permission',
        'ElevatedPermission',
        'WindowManifest',
        'WindowSize',
      ],
    },
  },
  {
    id: 'files',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': [
        'FileRef',
        'FileLocation',
        'FileKind',
        'FileEntry',
        'ShareInfo',
        'SortKey',
        'SortOrder',
        'FolderPage',
        'FileItems',
        'FileSelection',
        'ConflictPolicy',
        'FileOperation',
        'JobState',
        'JobInfo',
        'TrashEntry',
        'TrashPage',
        'FileVersion',
        'FolderChange',
        'UploadProgress',
        'StorageInfo',
        'StorageState',
        'StorageRequest',
        'FileHandler',
      ],
    },
  },
  {
    id: 'backend',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': [
        'BackendInfo',
        'BackendStatus',
        'BackendToken',
        'BackendTicket',
        'BackendManifest',
        'BackendEndpoint',
        'BackendHealth',
        'BackendSchedule',
      ],
    },
  },
  {
    id: 'events-context',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': [
        'AppContext',
        'LaunchArgs',
        'GroupRef',
        'SizeClass',
        'AppLogLevel',
        'Events',
        'EventName',
        'EventHandler',
        'Queries',
        'QueryName',
        'QueryHandler',
        'UnloadReason',
        'BEFORE_CLOSE_TIMEOUT',
        'BEFORE_UNLOAD_TIMEOUT',
      ],
    },
  },
  {
    id: 'i18n',
    package: 'sdk',
    exports: {
      '@pithyx/sdk/i18n': '*',
    },
  },
  {
    id: 'theme',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': ['Theme', 'ThemeTokenName', 'themeTokenNames', 'applyTheme'],
    },
  },
  {
    id: 'host',
    package: 'sdk',
    exports: {
      '@pithyx/sdk/host': '*',
    },
  },
  {
    id: 'errors',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': ['PithyxError', 'Problem', 'isProblem', 'sdkErrorCodes', 'SdkErrorCode'],
    },
  },
  {
    id: 'button',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Button', 'ButtonProps', 'ButtonVariant', 'IconButton', 'IconButtonProps'],
    },
  },
  {
    id: 'checkbox',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Checkbox', 'CheckboxProps'],
    },
  },
  {
    id: 'switch',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Switch', 'SwitchProps'],
    },
  },
  {
    id: 'progress-bar',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['ProgressBar', 'ProgressBarProps'],
    },
  },
  {
    id: 'badge',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Badge'],
    },
  },
  {
    id: 'dialog',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Dialog', 'DialogProps'],
    },
  },
  {
    id: 'menu',
    package: 'ui',
    exports: {
      '@pithyx/ui': [
        'Menu',
        'MenuItem',
        'MenuItemProps',
        'MenuSeparator',
        'MenuPopover',
        'MenuTrigger',
        'useDeliberatePress',
      ],
    },
  },
  {
    id: 'context-menu',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['ContextMenuPopover'],
    },
  },
  {
    id: 'text-field',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['TextField', 'TextFieldProps'],
    },
  },
  {
    id: 'password-field',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['PasswordField', 'PasswordFieldProps'],
    },
  },
  {
    id: 'search-field',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['SearchField', 'SearchFieldProps'],
    },
  },
  {
    id: 'otp-field',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['OtpField'],
    },
  },
  {
    id: 'tooltip',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Tooltip', 'TooltipProps', 'TooltipTrigger'],
    },
  },
  {
    id: 'ui-provider',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['UiProvider', 'UiProviderProps'],
    },
  },
  {
    id: 'helpers',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['cx', 'uiTexts', 'UiTexts'],
    },
  },
  {
    id: 'native',
    package: 'ui',
    exports: {
      '@pithyx/ui/native': '*',
    },
  },
];
