---
question: 'Can I sign in with a passkey?'
category: security
tags: [passkey, webauthn, fido2, passwordless, fingerprint, face, sign-in]
order: 25
status: ready
---

Yes, to the desktop, on a device that trusts the box and with the box opened by its name (such as `https://pithyx.local:7484`, not by its address). Add one in the Pithyx menu under your name, **Passkeys**; it replaces the password and the two-factor code at the sign-in. A passkey works only under the name it was made for, and SMB, SFTP, WebDAV and FTP keep using the password.

Details: [Passkeys](/docs/user/account/#passkeys).
