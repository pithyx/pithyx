import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useEffect, useRef, type SubmitEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { copyText } from '../errors/copyText';
import { describeError } from '../errors/describe';
import { field } from '../forms';
import { ErrorNotice } from '../errors/ErrorNotice';

type User = components['schemas']['UserInfo'];

/** Renders the otpauth URI as a QR code. The generator is loaded only when this is shown. */
function QrCode({ uri }: { uri: string }) {
  const { t } = useTranslation('auth');
  const image = useQuery({
    queryKey: ['qr', uri],
    queryFn: async () => {
      const { default: qrcode } = await import('qrcode-generator');
      const qr = qrcode(0, 'M');
      qr.addData(uri);
      qr.make();
      return qr.createDataURL(5, 4);
    },
    staleTime: Infinity,
  });
  return image.data === undefined ? null : (
    <img className="qr" src={image.data} alt={t('totpSetup.qrAlt')} width={180} height={180} />
  );
}

export function TotpSetup({ user, onClose }: { user: User; onClose: () => void }) {
  const { t } = useTranslation('auth');
  const queryClient = useQueryClient();
  const dialog = useRef<HTMLDialogElement>(null);

  const enroll = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/auth/totp/enroll')),
  });
  const confirm = useMutation({
    mutationFn: async (code: string) =>
      unwrap(await api.POST('/api/auth/totp/confirm', { body: { code } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.session });
    },
  });
  const disable = useMutation({
    mutationFn: async (password: string) => {
      unwrap(await api.POST('/api/auth/totp/disable', { body: { password } }));
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.session });
    },
  });

  // Opens the native modal dialog once it is in the DOM.
  useEffect(() => {
    dialog.current?.showModal();
  }, []);

  function submitCode(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    confirm.mutate(field(new FormData(event.currentTarget), 'code').trim());
  }

  function submitDisable(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    disable.mutate(field(new FormData(event.currentTarget), 'password'));
  }

  let content;
  if (confirm.data !== undefined) {
    const codes = confirm.data.codes;
    content = (
      <>
        <h3>{t('totpSetup.recoveryCodesTitle')}</h3>
        <p>{t('totpSetup.recoveryCodesIntro')}</p>
        <ul className="recovery-codes">
          {codes.map((code) => (
            <li key={code}>
              <code>{code}</code>
            </li>
          ))}
        </ul>
        <button type="button" className="button" onClick={() => void copyText(codes.join('\n'))}>
          {t('totpSetup.copyCodes')}
        </button>
      </>
    );
  } else if (user.totpEnabled) {
    content = (
      <form onSubmit={submitDisable}>
        <p>{t('totpSetup.on')}</p>
        <p className="field">
          <label htmlFor="totp-disable-password">{t('totpSetup.disablePassword')}</label>
          <input
            id="totp-disable-password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
          />
        </p>
        {disable.isError && <ErrorNotice error={describeError(disable.error)} />}
        <button type="submit" className="button" disabled={disable.isPending}>
          {t('totpSetup.disable')}
        </button>
      </form>
    );
  } else if (enroll.data !== undefined) {
    content = (
      <form onSubmit={submitCode}>
        <p>{t('totpSetup.scan')}</p>
        <QrCode uri={enroll.data.uri} />
        <p>
          {t('totpSetup.secret')} <code className="mono">{enroll.data.secret}</code>
        </p>
        <p className="field">
          <label htmlFor="totp-setup-code">{t('totpSetup.code')}</label>
          <input
            id="totp-setup-code"
            name="code"
            autoComplete="one-time-code"
            inputMode="numeric"
            pattern="[0-9 ]{6,7}"
            required
          />
        </p>
        {confirm.isError && <ErrorNotice error={describeError(confirm.error)} />}
        <button type="submit" className="button button--primary" disabled={confirm.isPending}>
          {t('totpSetup.confirm')}
        </button>
      </form>
    );
  } else {
    content = (
      <>
        <p>{t('totpSetup.off')}</p>
        {enroll.isError && <ErrorNotice error={describeError(enroll.error)} />}
        <button
          type="button"
          className="button button--primary"
          disabled={enroll.isPending}
          onClick={() => {
            enroll.mutate();
          }}
        >
          {t('totpSetup.start')}
        </button>
      </>
    );
  }

  return (
    <dialog ref={dialog} className="dialog" aria-labelledby="totp-setup-title" onClose={onClose}>
      <h2 id="totp-setup-title" className="dialog__title">
        {t('totpSetup.title')}
      </h2>
      {content}
      <form method="dialog" className="dialog__actions">
        <button type="submit" className="button">
          {t('totpSetup.close')}
        </button>
      </form>
    </dialog>
  );
}
