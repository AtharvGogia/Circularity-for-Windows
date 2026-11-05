-- One row per install per UTC day. Nothing else is kept: no IP address, no headers.
CREATE TABLE pings (
	day TEXT NOT NULL,
	id TEXT NOT NULL,
	version TEXT NOT NULL,
	PRIMARY KEY (day, id)
);
CREATE INDEX pings_day ON pings (day);
