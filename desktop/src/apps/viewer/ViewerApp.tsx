import type { FileEntry, PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { lazy, Suspense, useEffect, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../../ui/Button';
import {
  IconArrowLeft,
  IconArrowRight,
  IconDownload,
  IconMinus,
  IconPlus,
  IconZoomReset,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import type { ViewerTarget } from '../sdk/filesInternal';
import { contentUrl, readText } from '../sdk/viewerInternal';
import { viewKindOf } from './kinds';
import styles from './Viewer.module.css';

// pdf.js is large; it loads only when a PDF is opened.
const PdfView = lazy(() => import('./PdfView'));

const ZOOMS = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4] as const;
/** Folders this large are not walked for previous and next beyond this many entries. */
const MAX_SIBLINGS = 5000;

function targetOf(session: unknown): ViewerTarget | null {
  if (typeof session !== 'object' || session === null) {
    return null;
  }
  const { share, folder, name } = session as Record<string, unknown>;
  return typeof share === 'string' && typeof folder === 'string' && typeof name === 'string'
    ? { share, folder, name }
    : null;
}

/** The files of the folder the viewer can show, in name order, for previous and next. */
async function siblingsOf(app: PithyxApp, target: ViewerTarget): Promise<FileEntry[]> {
  const found: FileEntry[] = [];
  let cursor: string | undefined;
  do {
    const page = await app.files.list({ share: target.share }, target.folder, {
      limit: 1000,
      sort: 'name',
      order: 'asc',
      ...(cursor === undefined ? {} : { cursor }),
    });
    found.push(
      ...page.entries.filter((entry) => entry.kind === 'file' && viewKindOf(entry.name) !== null),
    );
    cursor = page.nextCursor ?? undefined;
  } while (cursor !== undefined && found.length < MAX_SIBLINGS);
  return found;
}

/**
 * Shows one file of a share in a window: images with zoom, PDF, text and code as plain text, audio and video with
 * the browser's own players (range requests). Nothing in a file is ever run: HTML and SVG sources are text, SVG
 * pictures are images, and every file is served with the sandbox policy. The arrow keys go to the previous and next
 * file of the folder.
 */
export function ViewerApp({ app }: SdkAppProps) {
  const { t } = useTranslation('viewer');
  const [target, setTarget] = useState<ViewerTarget | null>(() => targetOf(app.context.session));
  const [zoom, setZoom] = useState<number | 'fit'>('fit');
  const [failed, setFailed] = useState<string | null>(null);
  const siblings = useQuery({
    queryKey: ['viewer', 'siblings', target?.share, target?.folder],
    queryFn: () => (target === null ? [] : siblingsOf(app, target)),
    enabled: target !== null,
    staleTime: 30_000,
  });

  // The window title and the saved session follow the file on screen (both live outside React).
  useEffect(() => {
    if (target !== null) {
      void app.window.setTitle(target.name);
      void app.session.save(target);
    }
  }, [app, target]);

  if (target === null) {
    return <p className={styles.message}>{t('nothing')}</p>;
  }
  const kind = viewKindOf(target.name);
  const list = siblings.data ?? [];
  const index = list.findIndex((entry) => entry.name === target.name);
  const url = contentUrl(target.share, target.folder, target.name);

  function show(offset: number) {
    const next = list[index + offset];
    if (next !== undefined && target !== null) {
      setTarget({ ...target, name: next.name });
      setZoom('fit');
      setFailed(null);
    }
  }

  function zoomBy(step: number) {
    const current = zoom === 'fit' ? 1 : zoom;
    const position = ZOOMS.findIndex((value) => value >= current);
    const next =
      ZOOMS[
        Math.max(0, Math.min(ZOOMS.length - 1, (position < 0 ? ZOOMS.length - 1 : position) + step))
      ];
    setZoom(next ?? 1);
  }

  function onKeyDown(event: KeyboardEvent) {
    // Text fields and the media controls keep their keys.
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLMediaElement) {
      return;
    }
    const handled = (() => {
      switch (event.key) {
        case 'ArrowLeft':
          show(-1);
          return true;
        case 'ArrowRight':
          show(1);
          return true;
        case '+':
        case '=':
          if (kind === 'image') {
            zoomBy(1);
            return true;
          }
          return false;
        case '-':
          if (kind === 'image') {
            zoomBy(-1);
            return true;
          }
          return false;
        case '0':
          if (kind === 'image') {
            setZoom('fit');
            return true;
          }
          return false;
        default:
          return false;
      }
    })();
    if (handled) {
      event.preventDefault();
    }
  }

  return (
    <div className={styles.viewer} onKeyDown={onKeyDown}>
      <div className={styles.toolbar} role="toolbar" aria-label={t('toolbar')}>
        <IconButton
          label={t('previous')}
          size="sm"
          isDisabled={index <= 0}
          onPress={() => {
            show(-1);
          }}
        >
          <IconArrowLeft size={18} stroke={1.75} />
        </IconButton>
        <IconButton
          label={t('next')}
          size="sm"
          isDisabled={index < 0 || index >= list.length - 1}
          onPress={() => {
            show(1);
          }}
        >
          <IconArrowRight size={18} stroke={1.75} />
        </IconButton>
        <span className={styles.position}>
          {index >= 0 && list.length > 1
            ? t('position', { index: index + 1, count: list.length })
            : ''}
        </span>
        {kind === 'image' && (
          <>
            <IconButton
              label={t('zoomOut')}
              size="sm"
              onPress={() => {
                zoomBy(-1);
              }}
            >
              <IconMinus size={18} stroke={1.75} />
            </IconButton>
            <span className={styles.zoom} aria-live="polite">
              {zoom === 'fit' ? t('fit') : t('percent', { value: Math.round(zoom * 100) })}
            </span>
            <IconButton
              label={t('zoomIn')}
              size="sm"
              onPress={() => {
                zoomBy(1);
              }}
            >
              <IconPlus size={18} stroke={1.75} />
            </IconButton>
            <IconButton
              label={t('fitWindow')}
              size="sm"
              onPress={() => {
                setZoom('fit');
              }}
            >
              <IconZoomReset size={18} stroke={1.75} />
            </IconButton>
          </>
        )}
        <IconButton
          label={t('download')}
          size="sm"
          onPress={() => {
            void app.files.download(
              { share: target.share },
              target.folder === '' ? target.name : `${target.folder}/${target.name}`,
            );
          }}
        >
          <IconDownload size={18} stroke={1.75} />
        </IconButton>
      </div>
      {/* A scrolling region must be reachable with the keyboard. */}
      <div className={styles.stage} role="region" aria-label={t('stage')} tabIndex={0}>
        {failed !== null ? (
          <p className={styles.message}>{failed}</p>
        ) : kind === 'image' ? (
          <img
            key={url}
            src={url}
            alt={target.name}
            className={zoom === 'fit' ? styles.imageFit : styles.image}
            style={zoom === 'fit' ? undefined : { width: `${String(zoom * 100)}%` }}
            onError={() => {
              setFailed(t('failed'));
            }}
          />
        ) : kind === 'pdf' ? (
          <Suspense fallback={<p className={styles.message}>{t('loading')}</p>}>
            <PdfView
              key={url}
              url={url}
              onFailed={() => {
                setFailed(t('failed'));
              }}
            />
          </Suspense>
        ) : kind === 'text' ? (
          <TextView key={url} app={app} target={target} />
        ) : kind === 'audio' ? (
          <audio key={url} src={url} controls className={styles.audio} aria-label={target.name} />
        ) : kind === 'video' ? (
          <video key={url} src={url} controls className={styles.video} aria-label={target.name} />
        ) : (
          <p className={styles.message}>{t('unsupported')}</p>
        )}
      </div>
    </div>
  );
}

function TextView({ app, target }: { app: PithyxApp; target: ViewerTarget }) {
  const { t } = useTranslation('viewer');
  const text = useQuery({
    queryKey: ['viewer', 'text', target.share, target.folder, target.name],
    queryFn: () => readText(app, target.share, target.folder, target.name),
  });
  if (text.isError) {
    return <p className={styles.message}>{t('failed')}</p>;
  }
  if (text.data === undefined) {
    return <p className={styles.message}>{t('loading')}</p>;
  }
  return (
    <>
      {text.data.truncated && <p className={styles.note}>{t('truncated')}</p>}
      {/* Only ever a text node: markup in the file is shown, never parsed. */}
      <pre className={styles.text}>{text.data.text}</pre>
    </>
  );
}
