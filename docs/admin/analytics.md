---
title: Analytics
parent: Admin
nav_order: 6
---

# Analytics
{: .no_toc }

1. TOC
{:toc}

**Analytics** in the admin shows who visits the public site: how many people, which pages, where they came from,
which countries, screens, browsers and systems. It is built in: nothing to sign up for and nothing sent to another
company. It sets no cookies and stores no addresses; whether your site needs a consent notice depends on the law
where you and your visitors are.

Viewing needs `analytics: ro`; changing the settings and deleting the data needs `analytics: rw` (see
[Permissions](../permissions.md)). Administrators have both.

## What you see

| | |
|---|---|
| **Visitors now** | People on the site in the last 5 minutes (refreshes every 30 seconds) |
| **Unique visitors** | Different people in the period (see [How visitors are counted](#how-visitors-are-counted)) |
| **Visits** | Page views of one visitor less than 30 minutes apart are one visit |
| **Page views** | Every public page loaded |
| **Views per visit**, **Bounce rate** | Average pages per visit; share of visits that saw one page |
| **Visit duration** | Average time on the site per visit, from the time pages were actually visible |
| **Chart** | Visitors and page views per hour (one day), day (up to three months) or month |
| **Pages** | Top pages, and entry pages (where visits started) |
| **Sources** | Where visits came from: a known name (Google, Reddit, Hacker News, …), the referring site, or the `utm_source` tag; **Channels** groups them (Direct, Search, Social, Email, Paid, Referral, Campaign); **Campaigns** lists `utm_campaign` |
| **Countries** | From the visitor's address, looked up on your server (see [Countries](#countries)) |
| **Devices** | Screen (Mobile, Tablet, Laptop, Desktop, from the browser window width), browser and operating system |

Each number shows the change against the period before (the same length, just earlier). While a period is still
running (today, this month), it is compared with the same elapsed time before it: today until 14:00 against
yesterday until 14:00.

- **Period**: today, yesterday, last 7 or 30 days, this or last month, last 12 months, or custom dates.
- **Filters**: click any row (a page, a source, a country, …) to see only those visits; every number, the chart and
  the other lists follow. Filters show as chips at the top; ✕ removes one. The period and filters are in the URL, so a
  view can be bookmarked.
- **Export CSV** under each list downloads it in full (up to 1000 rows) for the period and filters.
- The **Dashboard** shows *Visitors today*.

Pages after the first in a visit keep the visit's source: a visitor who came from Google and then opened three more
pages counts as four page views from Google.

## Campaign links

Add UTM tags to links you share, and they show under Sources and Campaigns:

```
https://example.com/blog/launch?utm_source=newsletter&utm_medium=email&utm_campaign=autumn
```

`utm_medium` decides the channel: `email` or `newsletter` → Email, `cpc`, `ppc` or `paid` → Paid, `social` → Social.
`?ref=name` works as a short `utm_source`.

## How visitors are counted

A small script (`/js/hp-analytics.js`, under 1 KB) on every public page sends one message per page view to
`/hp/event`, and the time the page was visible when the visitor leaves.

- **No cookies** and nothing stored in the visitor's browser.
- **Addresses are never stored.** A visitor is a hash of their address, browser and the site's address with a random
  salt. The salt changes every day (at midnight in the analytics time zone) and the old one is deleted, so the hash
  cannot be turned back into an address, and the same person on two days cannot be linked. That is why unique
  visitors are counted **per day**: over a week, someone who came on three days counts three times.
- The address is used only for the hash and the country, in memory, and never written anywhere.
- **Not counted:** known bots and crawlers, browsers driven by automation, visits without JavaScript, pages shown
  inside frames (the editor and theme previews), error pages, and the admin.
- **Your own visits:** *Don't count my visits* on the Analytics page switches counting off in that browser (it sets a
  flag the script reads). Do it in each browser you use.

Numbers are close, not exact: privacy tools that block scripts, and several people behind one address with the same
browser, make the counts lower.

## Countries

With **Look up countries** on (the default), Homepage downloads the free
[DB-IP Lite](https://db-ip.com/db/lite.php) country database (about 8 MB, CC BY 4.0) to `data/geo/` shortly after
start, and again every month. Addresses are looked up on your server; nothing is sent anywhere. Visits before the
first download, and private addresses, show as *Unknown*.

To use another database, such as MaxMind GeoLite2, set `Analytics:GeoDatabase` to its `.mmdb` file (see
[Configuration](../server/configuration.md#analytics)); Homepage then reads that file and downloads nothing.

{: .note }
Behind a reverse proxy, enable `Site:ForwardedHeaders` (see [Configuration](../server/configuration.md)). Without
it every visit seems to come from the proxy: no countries, visitors with the same browser count as one, and all of
them share one limit of 120 messages per minute, so busy sites lose counts.

## Settings

On the Analytics page (needs `analytics: rw`):

| Setting | Default | |
|---|---|---|
| Count visits | on | Off: no script on the pages and nothing collected. The stored data stays. |
| Look up countries | on | Off: no country database download, countries are not stored for new visits |
| Keep visits for | 730 days | 30 to 3650. Older page views are deleted automatically. |
| Time zone | UTC | Where days and hours start in the reports, and when the daily salt changes. An IANA name: `Europe/Warsaw`, `America/New_York`, … |

**Delete all analytics data** removes every stored page view (it asks first). Settings changes and deletions are in
the audit log.
