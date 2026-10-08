import { createServer } from 'node:http';

createServer((request, response) => response.end('ok')).listen(8080);
