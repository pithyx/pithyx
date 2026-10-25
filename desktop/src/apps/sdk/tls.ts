import { PithyxApiError, unwrap, type components } from '@pithyx/api-client';
import type { MethodName, PithyxApp } from '@pithyx/sdk';
import { PithyxError, type HostExtensions } from '@pithyx/sdk/host';
import { api } from '../../api/client';

export type TlsSettings = components['schemas']['TlsSettings'];
export type CertificateInfo = components['schemas']['CertificateInfo'];
export type IssuedCertificate = components['schemas']['IssuedCertificate'];
export type CertificateKeyType = components['schemas']['CertificateKeyType'];
export type IssueCertificateRequest = components['schemas']['IssueCertificateRequest'];

/** Certificates for administrators, outside the public SDK; the daemon checks the right on every request. */
interface TlsMethods {
  'internal.admin.tls.settings': { params: Record<string, never>; result: TlsSettings };
  'internal.admin.tls.setNames': { params: { extraNames: string[] }; result: TlsSettings };
  'internal.admin.tls.createAuthority': {
    params: { restricted: boolean; domains: string[] };
    result: TlsSettings;
  };
  'internal.admin.tls.issue': { params: IssueCertificateRequest; result: IssuedCertificate };
  'internal.admin.tls.downloadIssued': { params: { id: number }; result: undefined };
  'internal.admin.tls.deleteIssued': { params: { id: number }; result: undefined };
  'internal.admin.tls.setCustom': {
    params: { certificatePem: string; privateKeyPem: string };
    result: TlsSettings;
  };
  'internal.admin.tls.removeCustom': { params: Record<string, never>; result: TlsSettings };
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isStrings = (value: unknown): value is string[] =>
  Array.isArray(value) && value.every((item) => typeof item === 'string');

async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw error instanceof PithyxApiError
      ? new PithyxError({
          code: error.code,
          params: error.params,
          detail: error.message,
          requestId: error.requestId,
        })
      : error;
  }
}

function save(text: string, fileName: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/x-pem-file' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  // The download has its own copy once the click went through.
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}

/** A file name from a certificate's first name: `*.media.lan` becomes `wildcard.media.lan`. */
function baseName(certificate: CertificateInfo): string {
  const first = certificate.names[0] ?? 'certificate';
  return first.replace(/^\*\./, 'wildcard.').replace(/[^a-zA-Z0-9.-]/g, '_');
}

/**
 * The host side of the Certificates page of the Settings app. An issued certificate's files go straight to the
 * browser's downloads; its private key never reaches the page and the box does not keep it.
 */
export function tlsHost(): HostExtensions {
  return {
    'internal.admin.tls.settings': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.GET('/api/system/tls'))),
    },
    'internal.admin.tls.setNames': {
      validate: (value) => isRecord(value) && isStrings(value.extraNames),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/system/tls/names', {
              body: params as { extraNames: string[] },
            }),
          ),
        ),
    },
    'internal.admin.tls.createAuthority': {
      validate: (value) =>
        isRecord(value) && typeof value.restricted === 'boolean' && isStrings(value.domains),
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.POST('/api/system/tls/authority', {
              body: params as { restricted: boolean; domains: string[] },
            }),
          ),
        ),
    },
    'internal.admin.tls.issue': {
      validate: (value) =>
        isRecord(value) &&
        isStrings(value.names) &&
        typeof value.validityDays === 'number' &&
        (value.keyType === 'ecdsa' || value.keyType === 'rsa'),
      handle: (params) =>
        daemon(async () => {
          const files = unwrap(
            await api.POST('/api/system/tls/issued', { body: params as IssueCertificateRequest }),
          );
          const name = baseName(files.certificate.certificate);
          save(files.certificatePem, `${name}.crt`);
          save(files.privateKeyPem, `${name}.key`);
          return files.certificate;
        }),
    },
    'internal.admin.tls.downloadIssued': {
      validate: (value) => isRecord(value) && typeof value.id === 'number',
      handle: (params) =>
        daemon(async () => {
          const { id } = params as { id: number };
          const pem = unwrap(
            await api.GET('/api/system/tls/issued/{id}/certificate.pem', {
              params: { path: { id } },
              parseAs: 'text',
            }),
          );
          save(pem, `certificate-${String(id)}.crt`);
          return undefined;
        }),
    },
    'internal.admin.tls.deleteIssued': {
      validate: (value) => isRecord(value) && typeof value.id === 'number',
      handle: (params) =>
        daemon(async () => {
          const { id } = params as { id: number };
          unwrap(await api.DELETE('/api/system/tls/issued/{id}', { params: { path: { id } } }));
          return undefined;
        }),
    },
    'internal.admin.tls.setCustom': {
      validate: (value) =>
        isRecord(value) &&
        typeof value.certificatePem === 'string' &&
        typeof value.privateKeyPem === 'string',
      handle: (params) =>
        daemon(async () =>
          unwrap(
            await api.PUT('/api/system/tls/custom', {
              body: params as { certificatePem: string; privateKeyPem: string },
            }),
          ),
        ),
    },
    'internal.admin.tls.removeCustom': {
      validate: isRecord,
      handle: () => daemon(async () => unwrap(await api.DELETE('/api/system/tls/custom'))),
    },
  };
}

export async function tlsCall<M extends keyof TlsMethods>(
  app: PithyxApp,
  method: M,
  params: TlsMethods[M]['params'],
): Promise<TlsMethods[M]['result']> {
  // The SDK's call is typed to the public methods; the internal names only exist here.
  return (await app.call(
    method as unknown as MethodName,
    params as never,
  )) as TlsMethods[M]['result'];
}
