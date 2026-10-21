# Other websites on this server

Caddy serves YHDE from `deploy/Caddyfile`. To serve more websites from the
same server, put one Caddy file per site in this folder, named
`something.caddy`. Git ignores them, so updating YHDE never changes or
blocks on them. Don't edit `deploy/Caddyfile` itself: an update that changes
it would then stop with "Could not switch to that version".

```
# deploy/sites/example.caddy
example.com {
	encode zstd gzip
	root * /srv/example
	file_server
}

www.example.com {
	redir https://example.com{uri} 301
}
```

- Don't repeat YHDE's own addresses here. YHDE's addresses are SITES (and
  REDIRECT_FROM for redirects) in `deploy/.env`, set with `./yhde set`.
- Files a site needs (`/srv/example` above) or extra containers go in
  `deploy/compose.override.yaml`, which Docker Compose reads next to
  `compose.yaml` and git also ignores:

```
services:
  caddy:
    volumes:
      - /var/www/example:/srv/example:ro
```

- Check before applying, then restart Caddy (a few seconds offline for all sites):

```
docker compose --project-directory /opt/yhde/deploy exec caddy caddy validate --config /etc/caddy/Caddyfile
docker compose --project-directory /opt/yhde/deploy restart caddy
```
