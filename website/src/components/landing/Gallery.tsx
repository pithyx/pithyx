import { useEffect, useRef, useState } from 'react';
import type { Picture } from './types.ts';

// Thumbnails that open a lightbox; arrow keys step through it, Escape closes it (the dialog's own behavior).
export function Gallery({
  title,
  close,
  pictures,
}: {
  title: string;
  close: string;
  pictures: Picture[];
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [open, setOpen] = useState<number | undefined>(undefined);
  const current = open === undefined ? undefined : pictures[open];

  useEffect(() => {
    if (open === undefined) {
      dialog.current?.close();
    } else if (!dialog.current?.open) {
      dialog.current?.showModal();
    }
  }, [open]);

  const step = (by: number) => {
    setOpen((index) =>
      index === undefined ? index : (index + by + pictures.length) % pictures.length,
    );
  };

  return (
    <section className="px-section" aria-labelledby="px-gallery">
      <h2 id="px-gallery">{title}</h2>
      <ul className="px-gallery">
        {pictures.map((picture, index) => (
          <li key={picture.src}>
            <button
              type="button"
              onClick={() => {
                setOpen(index);
              }}
            >
              <img
                src={picture.src}
                width={picture.width}
                height={picture.height}
                alt={picture.alt}
                loading="lazy"
              />
              <span>{picture.caption}</span>
            </button>
          </li>
        ))}
      </ul>
      <dialog
        ref={dialog}
        className="px-lightbox"
        onClose={() => {
          setOpen(undefined);
        }}
        onKeyDown={(event) => {
          if (event.key === 'ArrowRight') step(1);
          if (event.key === 'ArrowLeft') step(-1);
        }}
        onClick={(event) => {
          if (event.target === dialog.current) setOpen(undefined);
        }}
      >
        {current && (
          <figure>
            <img
              src={current.src}
              width={current.width}
              height={current.height}
              alt={current.alt}
            />
            <figcaption>{current.caption}</figcaption>
          </figure>
        )}
        <button
          type="button"
          className="px-lightbox-close"
          onClick={() => {
            setOpen(undefined);
          }}
        >
          {close}
        </button>
      </dialog>
    </section>
  );
}
