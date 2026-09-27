import { IconCopy, IconShieldLock } from '../ui/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useRef, useState, type SyntheticEvent } from 'react';
import { Form } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { copyText } from '../platform/browser';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { field } from '../forms';
import { Button } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { OtpField, PasswordField } from '../ui/TextField';
import styles from './TotpSetup.module.css';

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
    <img
      className={styles.qr}
      src={image.data}
      alt={t('totpSetup.qrAlt')}
      width={180}
      height={180}
    />
  );
}

/** Turns TOTP on (QR code, first code, recovery codes) or off (with the password). */
export function TotpSetup({
  user,
  isOpen,
  onClose,
}: {
  user: User;
  isOpen: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation('auth');
  const queryClient = useQueryClient();
  const codeList = useRef<HTMLUListElement>(null);
  const [copyManually, setCopyManually] = useState(false);

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

  function close() {
    enroll.reset();
    confirm.reset();
    disable.reset();
    onClose();
  }

  function submitCode(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    confirm.mutate(field(new FormData(event.currentTarget), 'code').trim());
  }

  function submitDisable(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    disable.mutate(field(new FormData(event.currentTarget), 'password'));
  }

  let content;
  if (confirm.data !== undefined) {
    const codes = confirm.data.codes;
    content = (
      <>
        <h3 className={styles.subtitle}>{t('totpSetup.recoveryCodesTitle')}</h3>
        <p>{t('totpSetup.recoveryCodesIntro')}</p>
        <ul ref={codeList} className={styles.codes}>
          {codes.map((code) => (
            <li key={code}>
              <code>{code}</code>
            </li>
          ))}
        </ul>
        <Button
          onPress={() => {
            void copyText(codes.join('\n'), codeList.current).then((done) => {
              setCopyManually(!done);
            });
          }}
        >
          <IconCopy size={18} stroke={1.75} aria-hidden />
          {t('totpSetup.copyCodes')}
        </Button>
        <p className={styles.copyHint} role="status">
          {copyManually ? t('totpSetup.copyManually') : ''}
        </p>
      </>
    );
  } else if (user.totpEnabled) {
    content = (
      <Form className={styles.form} onSubmit={submitDisable} validationBehavior="aria">
        <p>{t('totpSetup.on')}</p>
        <PasswordField
          label={t('totpSetup.disablePassword')}
          name="password"
          autoComplete="current-password"
          autoFocus
          isRequired
        />
        {disable.isError && <ErrorNotice error={describeError(disable.error)} />}
        <Button type="submit" variant="danger" isPending={disable.isPending}>
          {t('totpSetup.disable')}
        </Button>
      </Form>
    );
  } else if (enroll.data !== undefined) {
    content = (
      <Form className={styles.form} onSubmit={submitCode} validationBehavior="aria">
        <p>{t('totpSetup.scan')}</p>
        <QrCode uri={enroll.data.uri} />
        <p className={styles.secret}>
          {t('totpSetup.secret')} <code>{enroll.data.secret}</code>
        </p>
        <OtpField label={t('totpSetup.code')} name="code" autoFocus isRequired />
        {confirm.isError && <ErrorNotice error={describeError(confirm.error)} />}
        <Button type="submit" variant="primary" isPending={confirm.isPending}>
          {t('totpSetup.confirm')}
        </Button>
      </Form>
    );
  } else {
    content = (
      <>
        <p>{t('totpSetup.off')}</p>
        {enroll.isError && <ErrorNotice error={describeError(enroll.error)} />}
        <Button
          variant="primary"
          isPending={enroll.isPending}
          onPress={() => {
            enroll.mutate();
          }}
        >
          {t('totpSetup.start')}
        </Button>
      </>
    );
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={(open) => {
        if (!open) {
          close();
        }
      }}
      isDismissable
      icon={<IconShieldLock size={22} stroke={1.75} />}
      title={t('totpSetup.title')}
      actions={<Button slot="close">{t('totpSetup.close')}</Button>}
    >
      {content}
    </Dialog>
  );
}
