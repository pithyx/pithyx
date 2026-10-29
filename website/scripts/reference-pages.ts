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
        'Permissions',
        'PermissionGrant',
        'Screenshot',
        'NotificationCategory',
        'WindowLoading',
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
        'ShareLocation',
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
        'FileAction',
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
        'BackendUnavailableReason',
        'BackendManifest',
        'BackendEndpoint',
        'BackendHealth',
        'BackendSchedule',
        'BackendBackup',
      ],
    },
  },
  {
    id: 'events-context',
    package: 'sdk',
    exports: {
      '@pithyx/sdk': [
        'AppContext',
        'FileActionLaunch',
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
    id: 'app-frame',
    package: 'ui',
    exports: {
      '@pithyx/ui': [
        'AppFrame',
        'AppFrameProps',
        'AppFrameView',
        'NavList',
        'NavListProps',
        'NavGroup',
        'NavItem',
      ],
    },
  },
  {
    id: 'toolbar',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Toolbar', 'ToolbarProps', 'ToolbarSeparator'],
    },
  },
  {
    id: 'tabs',
    package: 'ui',
    exports: {
      '@pithyx/ui': [
        'Tabs',
        'TabsProps',
        'TabList',
        'TabListProps',
        'Tab',
        'TabProps',
        'TabPanel',
        'TabPanelProps',
      ],
    },
  },
  {
    id: 'data-table',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['DataTable', 'DataTableProps', 'DataTableColumn'],
    },
  },
  {
    id: 'form',
    package: 'ui',
    exports: {
      '@pithyx/ui': [
        'FormSection',
        'FormSectionProps',
        'FormRow',
        'FormRowProps',
        'ActionBar',
        'ActionBarProps',
      ],
    },
  },
  {
    id: 'radio-group',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['RadioGroup', 'RadioGroupProps', 'Radio', 'RadioProps'],
    },
  },
  {
    id: 'select',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Select', 'SelectProps', 'SelectOption'],
    },
  },
  {
    id: 'card',
    package: 'ui',
    exports: {
      '@pithyx/ui': ['Card', 'CardProps', 'Meter', 'MeterProps'],
    },
  },
  {
    id: 'status',
    package: 'ui',
    exports: {
      '@pithyx/ui': [
        'StatusBadge',
        'StatusTone',
        'AttentionList',
        'AttentionListProps',
        'AttentionItem',
        'SummaryTiles',
        'SummaryTile',
      ],
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
