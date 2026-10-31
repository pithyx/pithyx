# @pithyx/cli

[![npm](https://img.shields.io/npm/v/@pithyx/cli)](https://www.npmjs.com/package/@pithyx/cli)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](./LICENSE)

The `pithyx` command for building apps for [Pithyx](https://github.com/pithyx/pithyx), the free, self-hosted NAS and server OS with a web desktop.

## Commands

| Command           | What it does                                                                                                  |
| ----------------- | ------------------------------------------------------------------------------------------------------------- |
| `pithyx validate` | Checks an app with the same rules and error codes as the box                                                  |
| `pithyx lock`     | Pins the images of the app's backend by digest                                                                |
| `pithyx pack`     | Writes the `.pithyxapp` file the box installs; the same files give the same bytes                             |
| `pithyx dev`      | Runs the app on your machine in a mock of the desktop, with the box's sandbox                                 |
| `pithyx build`    | Builds the backend's own images and pins the app to them; `--oci-out` builds reproducibly for amd64 and arm64 |
| `pithyx catalog`  | The CI of a catalog repository: `check`, `pending`, `index`, `sign` and `verify`                              |

`pithyx --help` lists every option. The package also brings a Vite plugin (`@pithyx/cli/vite`) with the same mock desktop. There is no development mode on the box: to try an app there, upload its package in the Store.

## Install

```sh
npm install --save-dev @pithyx/cli
```

## Related packages

| Package                                                    | What it is                                            |
| ---------------------------------------------------------- | ----------------------------------------------------- |
| [`@pithyx/sdk`](https://www.npmjs.com/package/@pithyx/sdk) | The API between an app and the Pithyx desktop         |
| [`@pithyx/ui`](https://www.npmjs.com/package/@pithyx/ui)   | The React components and design tokens of the desktop |

## Entry points

| Import             | What it is                                 |
| ------------------ | ------------------------------------------ |
| `pithyx` (command) | The command line, see Commands             |
| `@pithyx/cli`      | The checks the command shares with the box |
| `@pithyx/cli/vite` | The Vite plugin with the mock desktop      |

## Documentation

The full documentation is on the docs site at [pithyx.org](https://pithyx.org):

- [The @pithyx/cli reference](https://pithyx.org/docs/sdk/cli/)
- [Your first app](https://pithyx.org/docs/developers/apps/first-app/)
- [Publishing an app](https://pithyx.org/docs/developers/apps/publishing/)
- [Apps overview](https://pithyx.org/docs/developers/apps/overview/)

Every page is also available as Markdown (add `.md` to its path), and [pithyx.org/llms.txt](https://pithyx.org/llms.txt) indexes them all.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one version line, separate from the Pithyx product version, and follow SemVer since `1.0.0`: a breaking change needs a major version. Releases are published under the dist-tag `latest`; a release candidate of a later version goes to `next` first.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
