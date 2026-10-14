"""
Builds src/PrayerBar/Data/cities.json - the bundled, offline country/city list.

Usage:
    python tools/build_cities.py <geonames-dir>

<geonames-dir> must contain these files from https://download.geonames.org/export/dump/ :
    cities15000.txt   (unzipped cities15000.zip)
    countryInfo.txt

What it does:
  1. Picks, per country: the capital, every first-level admin seat (province / state capital)
     and the most populous cities, up to MAX_CITIES (admin seats are always kept).
  2. Looks up Arabic names on Wikidata (by GeoNames id for cities, ISO code for countries).
     Falls back to an Arabic-script alternate name from GeoNames, then to the English name.
  3. Asks Aladhan which calculation method it uses by default at the country's capital
     (Aladhan picks the method whose home location is nearest). Turkey is pinned to 13.
"""
import csv
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

MAX_CITIES = 30
UA = "PrayerBar-data-builder/1.0 (https://github.com/; offline city list)"
OUT = Path(__file__).resolve().parent.parent / "src" / "PrayerBar" / "Data" / "cities.json"
CACHE = Path(__file__).resolve().parent / ".cache"

# Arabic names in everyday use that differ from Wikidata's label (key: GeoNames English name).
AR_OVERRIDES = {
    ("TR", "Gaziantep"): "غازي عنتاب",
    ("TR", "Sanliurfa"): "أورفة",
    ("TR", "Şanlıurfa"): "أورفة",
    ("TR", "Kahramanmaras"): "كهرمان مرعش",
    ("TR", "Kahramanmaraş"): "كهرمان مرعش",
    ("DE", "Munich"): "ميونخ",
    ("DE", "Koeln"): "كولونيا",
}

# Letters used by Persian / Urdu / Pashto but not Arabic - used to reject non-Arabic spellings.
NON_ARABIC = re.compile("[پچژگکیۀٹڈڑںھےۓڭګ]")
ARABIC = re.compile("^[؀-ۿ\\s\\-'ʼ]+$")


def http_json(url, data=None):
    req = urllib.request.Request(url, data=data, headers={"User-Agent": UA, "Accept": "application/json"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except Exception as e:  # noqa: BLE001 - retry anything transient
            if attempt == 4:
                raise
            print(f"  retry after {e}", file=sys.stderr)
            time.sleep(3 * (attempt + 1))


def cached(name, producer):
    CACHE.mkdir(exist_ok=True)
    path = CACHE / name
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    value = producer()
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    return value


def sparql(query):
    data = urllib.parse.urlencode({"query": query, "format": "json"}).encode()
    return http_json("https://query.wikidata.org/sparql", data)["results"]["bindings"]


def load_countries(geo):
    countries = {}
    with open(geo / "countryInfo.txt", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#"):
                continue
            p = line.rstrip("\n").split("\t")
            countries[p[0]] = {"code": p[0], "en": p[4], "capital": p[5]}
    return countries


def load_cities(geo):
    cities = []
    with open(geo / "cities15000.txt", encoding="utf-8") as f:
        for p in csv.reader(f, delimiter="\t", quoting=csv.QUOTE_NONE):
            cities.append({
                "id": p[0], "en": p[2] or p[1], "alt": p[3].split(",") if p[3] else [],
                "lat": float(p[4]), "lng": float(p[5]), "fcode": p[7], "cc": p[8],
                "pop": int(p[14] or 0), "tz": p[17],
            })
    return cities


def select(cities_by_cc):
    chosen = {}
    for cc, items in cities_by_cc.items():
        items.sort(key=lambda c: -c["pop"])
        must = [c for c in items if c["fcode"] in ("PPLC", "PPLA")]
        rest = [c for c in items if c["fcode"] not in ("PPLC", "PPLA")]
        # Always keep the largest cities too: in countries with many admin seats (US states)
        # the seats alone would crowd out New York, Los Angeles, etc.
        picked = must + rest[:MAX_CITIES]
        # De-duplicate by name (some countries list the same city twice under different codes).
        seen, unique = set(), []
        for c in sorted(picked, key=lambda c: -c["pop"]):
            if c["en"].lower() not in seen:
                seen.add(c["en"].lower())
                unique.append(c)
        chosen[cc] = unique
    return chosen


def arabic_city_names(ids):
    names = {}
    ids = sorted(ids)
    for i in range(0, len(ids), 400):
        batch = ids[i:i + 400]
        values = " ".join(f'"{x}"' for x in batch)
        q = f"""SELECT ?gid ?label WHERE {{
                  VALUES ?gid {{ {values} }}
                  ?item wdt:P1566 ?gid ; rdfs:label ?label .
                  FILTER(LANG(?label) = "ar") }}"""
        for row in sparql(q):
            names.setdefault(row["gid"]["value"], row["label"]["value"])
        print(f"  wikidata cities {i + len(batch)}/{len(ids)}", file=sys.stderr)
        time.sleep(1)
    return names


def arabic_country_names():
    q = """SELECT ?code ?label WHERE {
             ?c wdt:P297 ?code ; rdfs:label ?label .
             FILTER(LANG(?label) = "ar") }"""
    names = {}
    for row in sparql(q):
        names.setdefault(row["code"]["value"], row["label"]["value"])
    return names


def aladhan_default_method(lat, lng):
    url = f"https://api.aladhan.com/v1/timings/15-01-2026?latitude={lat}&longitude={lng}"
    return http_json(url)["data"]["meta"]["method"]["id"]


def fallback_arabic(city):
    for name in city["alt"]:
        if ARABIC.match(name) and not NON_ARABIC.search(name):
            return name
    return None


def main():
    geo = Path(sys.argv[1])
    countries = load_countries(geo)
    by_cc = {}
    for c in load_cities(geo):
        if c["cc"] in countries:
            by_cc.setdefault(c["cc"], []).append(c)
    chosen = select(by_cc)

    all_ids = [c["id"] for items in chosen.values() for c in items]
    ar_cities = cached("ar_cities.json", lambda: arabic_city_names(all_ids))
    missing = [i for i in all_ids if i not in ar_cities]
    if missing:
        ar_cities.update(arabic_city_names(missing))
        (CACHE / "ar_cities.json").write_text(json.dumps(ar_cities, ensure_ascii=False), encoding="utf-8")
    ar_countries = cached("ar_countries.json", arabic_country_names)

    def probe_methods():
        result = {}
        for cc, items in sorted(chosen.items()):
            cap = next((c for c in items if c["fcode"] == "PPLC"), items[0])
            result[cc] = aladhan_default_method(cap["lat"], cap["lng"])
            print(f"  method {cc} -> {result[cc]}", file=sys.stderr)
            time.sleep(0.25)
        return result
    methods = cached("methods.json", probe_methods)
    methods["TR"] = 13  # Diyanet, per requirements

    out = []
    for cc, items in chosen.items():
        info = countries[cc]
        out.append({
            "code": cc,
            "en": info["en"],
            "ar": ar_countries.get(cc, info["en"]),
            "method": methods.get(cc, 3),
            "cities": [{
                "en": c["en"],
                "ar": AR_OVERRIDES.get((cc, c["en"])) or ar_cities.get(c["id"]) or fallback_arabic(c) or c["en"],
                "lat": round(c["lat"], 4),
                "lng": round(c["lng"], 4),
                "tz": c["tz"],
            } for c in items],
        })
    out.sort(key=lambda c: c["en"])
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({"version": 1, "countries": out}, ensure_ascii=False, separators=(",", ":")),
                   encoding="utf-8")
    total = sum(len(c["cities"]) for c in out)
    print(f"{OUT}: {len(out)} countries, {total} cities, {OUT.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
