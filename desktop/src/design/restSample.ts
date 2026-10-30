import { api } from '../api/client';

// The two-factor setup of Personal settings talks to the daemon over REST instead of the SDK; its two calls get sample
// answers here. The secret and the codes are invented.
const answers: Record<string, unknown> = {
  'POST /api/auth/totp/enroll': {
    secret: 'K5QW4ZLSEBSXQYLNOBWGKIDQNF2GQ6LY',
    uri: 'otpauth://totp/Pithyx:ada?secret=K5QW4ZLSEBSXQYLNOBWGKIDQNF2GQ6LY&issuer=Pithyx',
  },
  'POST /api/auth/totp/confirm': {
    codes: [
      'HY8E6-V8S99',
      'R7C2H-KRQC9',
      'MZYC4-1SX7P',
      'EK6BX-8XH7X',
      'NEV7Q-4YES8',
      'BZPZZ-1D9RA',
      'DP9NK-TF7J8',
      '9ASRN-QXVP2',
      '2RNCZ-J5EJS',
      'F78Z6-FYYR6',
    ],
  },
};

/** Answers the REST calls above in the gallery; every other request goes on as before. */
export function answerRestWithSamples() {
  api.use({
    onRequest({ request }) {
      const body = answers[`${request.method} ${new URL(request.url).pathname}`];
      return body === undefined ? undefined : Response.json(body);
    },
  });
}
