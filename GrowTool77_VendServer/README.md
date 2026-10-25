# GrowTool77 Vend Server

Lokaali Ruby-palvelin, joka tallentaa GrowTool77:n keräämät vend-tiedot SQLite-tietokantaan.

## Asennus

1. Asenna Ruby (https://rubyinstaller.org/ Windowsille)
2. Avaa komentorivi tähän kansioon
3. `gem install bundler`
4. `bundle install`
5. `ruby server.rb`

Palvelin käynnistyy osoitteeseen http://localhost:4567

## API

- `GET  /ping`                    — terveystarkistus
- `POST /worlds/:name/vends`      — tallenna maailman vend-data
- `GET  /worlds/:name/vends`      — hae maailman vend-data
- `GET  /vends/search?item_id=X`  — halvin hinta kaikista maailmoista
- `GET  /worlds`                  — lista kaikista maailmoista
- `DELETE /worlds/stale?days=30`  — poista yli 30pv vanhat

## Tietokanta

Tallentuu samaan kansioon: `vends.db`
