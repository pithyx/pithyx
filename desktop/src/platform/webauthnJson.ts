// WebAuthn's binary options and answers as the JSON the daemon speaks (base64url strings, D380). Pure conversion, no
// browser API: browser.ts calls the authenticator and loads this module only then.

interface JsonDescriptor {
  id: string;
  type: 'public-key';
  transports?: AuthenticatorTransport[];
}

/** The options of `POST /api/auth/passkeys/options`. */
export interface PasskeyCreationOptions {
  challenge: string;
  rp: { id?: string; name: string };
  user: { id: string; name: string; displayName: string };
  pubKeyCredParams: { type: 'public-key'; alg: number }[];
  timeout?: number;
  excludeCredentials?: JsonDescriptor[];
  authenticatorSelection?: AuthenticatorSelectionCriteria;
  attestation?: AttestationConveyancePreference;
}

/** The options of `POST /api/auth/passkeys/sign-in/options`. */
export interface PasskeyRequestOptions {
  challenge: string;
  rpId?: string;
  timeout?: number;
  allowCredentials?: JsonDescriptor[];
  userVerification?: UserVerificationRequirement;
}

export function creationOptions(
  options: PasskeyCreationOptions,
): PublicKeyCredentialCreationOptions {
  return {
    challenge: fromBase64Url(options.challenge),
    rp: options.rp,
    user: { ...options.user, id: fromBase64Url(options.user.id) },
    pubKeyCredParams: options.pubKeyCredParams,
    ...(options.timeout === undefined ? {} : { timeout: options.timeout }),
    excludeCredentials: (options.excludeCredentials ?? []).map(descriptor),
    ...(options.authenticatorSelection === undefined
      ? {}
      : { authenticatorSelection: options.authenticatorSelection }),
    attestation: options.attestation ?? 'none',
  };
}

export function requestOptions(options: PasskeyRequestOptions): PublicKeyCredentialRequestOptions {
  return {
    challenge: fromBase64Url(options.challenge),
    ...(options.rpId === undefined ? {} : { rpId: options.rpId }),
    ...(options.timeout === undefined ? {} : { timeout: options.timeout }),
    allowCredentials: (options.allowCredentials ?? []).map(descriptor),
    userVerification: options.userVerification ?? 'required',
  };
}

export function registrationJson(credential: PublicKeyCredential): object {
  const response = credential.response as AuthenticatorAttestationResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      attestationObject: toBase64Url(response.attestationObject),
      transports: response.getTransports(),
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}

export function assertionJson(credential: PublicKeyCredential): object {
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      authenticatorData: toBase64Url(response.authenticatorData),
      signature: toBase64Url(response.signature),
      ...(response.userHandle === null ? {} : { userHandle: toBase64Url(response.userHandle) }),
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}

function descriptor(entry: JsonDescriptor): PublicKeyCredentialDescriptor {
  return {
    id: fromBase64Url(entry.id),
    type: entry.type,
    ...(entry.transports === undefined ? {} : { transports: entry.transports }),
  };
}

export function fromBase64Url(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(base64 + '='.repeat((4 - (base64.length % 4)) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes.buffer;
}

export function toBase64Url(buffer: ArrayBuffer): string {
  let binary = '';
  for (const byte of new Uint8Array(buffer)) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
