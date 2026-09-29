import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import styles from './Viewer.module.css';

GlobalWorkerOptions.workerSrc = workerUrl;

/**
 * A PDF drawn page by page into canvases with pdf.js (Apache 2.0), loaded only when a PDF is opened. Pages are
 * drawn when they come near the view. Only pixels are drawn: no text layer, no forms, no links and no scripts.
 */
export default function PdfView({ url, onFailed }: { url: string; onFailed: () => void }) {
  const { t } = useTranslation('viewer');
  const [document, setDocument] = useState<PDFDocumentProxy | null>(null);
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);

  // Loading the document is synchronization with pdf.js and its worker, outside React.
  useEffect(() => {
    const task = getDocument({ url, enableXfa: false, isOffscreenCanvasSupported: true });
    let active = true;
    task.promise.then(
      (loaded) => {
        if (active) {
          setDocument(loaded);
        }
      },
      () => {
        if (active) {
          onFailed();
        }
      },
    );
    return () => {
      active = false;
      void task.destroy();
    };
  }, [url, onFailed]);

  // Pages are as wide as the view: syncing with the DOM.
  useEffect(() => {
    const element = container.current;
    if (element === null) {
      return;
    }
    const observer = new ResizeObserver(() => {
      setWidth(element.clientWidth);
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);

  return (
    <div ref={container} className={styles.pdf}>
      {document === null ? (
        <p className={styles.muted} role="status">
          {t('loading')}
        </p>
      ) : (
        Array.from({ length: document.numPages }, (_, index) => (
          <PdfPage
            key={index}
            document={document}
            number={index + 1}
            width={Math.max(200, width - 32)}
            label={t('page', { page: index + 1, count: document.numPages })}
          />
        ))
      )}
    </div>
  );
}

function PdfPage({
  document,
  number,
  width,
  label,
}: {
  document: PDFDocumentProxy;
  number: number;
  width: number;
  label: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [visible, setVisible] = useState(false);
  const [ratio, setRatio] = useState(Math.SQRT2);

  // Drawing starts when the page comes near the view: syncing with the browser's intersection observer.
  useEffect(() => {
    const element = canvas.current;
    if (element === null) {
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          setVisible(true);
        }
      },
      { rootMargin: '600px 0px' },
    );
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);

  // Drawing into the canvas is synchronization with pdf.js.
  useEffect(() => {
    const element = canvas.current;
    if (!visible || element === null || width <= 0) {
      return;
    }
    let cancelled = false;
    let render: { cancel: () => void } | null = null;
    void document.getPage(number).then((page) => {
      if (cancelled) {
        return;
      }
      const base = page.getViewport({ scale: 1 });
      setRatio(base.height / base.width);
      const pixels = window.devicePixelRatio || 1;
      const viewport = page.getViewport({ scale: (width / base.width) * pixels });
      element.width = Math.floor(viewport.width);
      element.height = Math.floor(viewport.height);
      const task = page.render({ canvas: element, viewport });
      render = task;
      task.promise.catch(() => undefined);
    });
    return () => {
      cancelled = true;
      render?.cancel();
    };
  }, [document, number, visible, width]);

  return (
    <canvas
      ref={canvas}
      className={styles.pdfPage}
      style={{ width, height: width * ratio }}
      role="img"
      aria-label={label}
    />
  );
}
