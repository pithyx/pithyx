# syntax=docker/dockerfile:1
FROM node:24-alpine@sha256:545ea538461003efdc8c81c244531b003f6f26cfccf6c0073b3239fdedf49446 AS build
WORKDIR /app
COPY package.json ./
RUN npm ci \
    --omit=dev
COPY . .

FROM gcr.io/distroless/nodejs24-debian13@sha256:ea3e6869841f102bdb95fc7358ad94097770136193d88ac06a56a1c5a318d613
COPY --from=build /app /app
CMD ["/app/index.js"]
