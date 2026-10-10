import type { Picture } from './types.ts';

export function Showcase({ picture }: { picture: Picture }) {
  return (
    <figure className="px-showcase">
      <img
        src={picture.src}
        width={picture.width}
        height={picture.height}
        alt={picture.alt}
        loading="eager"
      />
      <figcaption>{picture.caption}</figcaption>
    </figure>
  );
}
