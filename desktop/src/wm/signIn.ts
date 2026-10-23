// Set by the sign-in form, so a reload or a new tab with the session only restores the layout (D373). Its own module
// without imports: the sign-in screen sets it and must not load the window manager.
let afterSignIn = false;

/** Called when the user signed in with the form; the next desktop opens the startup apps once. */
export function markSignedIn(): void {
  afterSignIn = true;
}

export function isAfterSignIn(): boolean {
  return afterSignIn;
}

/** Whether this desktop follows a sign-in, once: the flag is cleared. */
export function takeSignIn(): boolean {
  const was = afterSignIn;
  afterSignIn = false;
  return was;
}
