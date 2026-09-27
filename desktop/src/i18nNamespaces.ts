// Shared with vite.config.ts, which puts each language's texts into chunks by when they are needed: the sign-in
// screen's namespaces before the first render (i18n-<lang>), the desktop shell's with the desktop
// (i18n-<lang>-desktop), and every other namespace with the app that uses it (i18n-<lang>-<namespace>).
export const signInNamespaces = ['desktop', 'auth', 'errors'] as const;
export const desktopNamespaces = ['notifications', 'jobs'] as const;
