#!/bin/sh
# Runs inside the `backup` container: a compressed pg_dump of the whole
# database once a day (and on start), keeping KEEP_DAYS days, plus a copy of
# every asset blob. Blobs never change once stored, so copying the new ones is
# a complete incremental backup; they are kept as long as the dumps that may
# reference them could be restored, i.e. forever.
#   /backup.sh        loop forever (the container's job)
#   /backup.sh once   take one backup now
set -u

take() {
	f="/backups/yhde-$(date -u +%Y%m%d-%H%M%S).dump"
	if pg_dump -Fc -f "$f.part"; then
		mv "$f.part" "$f"
		echo "backup: wrote $f ($(du -h "$f" | cut -f1))"
	else
		rm -f "$f.part"
		echo "backup: FAILED" >&2
		return 1
	fi
	find /backups -name 'yhde-*.dump' -mtime +"${KEEP_DAYS:-14}" -delete
	if [ -d /blobs/sha256 ]; then
		mkdir -p /backups/blobs
		if cp -Rup /blobs/sha256/. /backups/blobs/; then
			echo "backup: asset files $(du -sh /backups/blobs | cut -f1)"
		else
			echo "backup: asset copy FAILED" >&2
			return 1
		fi
	fi
}

if [ "${1:-}" = "once" ]; then
	take
	exit $?
fi

while true; do
	take || true
	sleep 86400
done
