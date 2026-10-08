# ZapMQ panel

The web interface of the ZapMQ administration panel: Angular, Angular Material and ECharts.

It is compiled into `../ZapMQ.Server/wwwroot` and travels inside the service executable; `publish.sh` at the repository root does both steps. What the panel is meant to be is in [`docs/PAINEL.md`](../../docs/PAINEL.md) (in Portuguese).

## Working on it

Start the service (`dotnet run --project src/ZapMQ.Server` from the repository root), then, here:

```
npm install
npm start
```

`http://localhost:4200` serves the interface with live reload and forwards `/api` to the panel port of the service (5680), as set in `proxy.conf.json`.

## Notes

- Every address the interface uses is relative to `<base href>`, which the service rewrites when it serves the page. That is what lets the panel work both at the root of its port and under the path of a reverse proxy.
- Icons are SVGs imported one by one in `src/app/core/icons.ts`; fonts come from npm packages. Nothing is fetched from the internet at run time.
