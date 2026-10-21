# GrowTool77 — Team Aura

## Käynnistys
1. Avaa `GrowTool77.sln` Visual Studiossa ja paina **F5**.
2. `aura_enet.dll` kopioituu automaattisesti ohjelman viereen.

## [AURA]->proxy — käyttö
1. **Main**-välilehti → **Start [AURA]**. Windows kysyy luvan (UAC) hosts-tiedoston muuttamiseen.
2. Käynnistä Growtopia (jos se oli jo auki, sulje ja avaa uudelleen).
3. Mene worldiin. **Current world** näyttää nimen, **World**-välilehti pudotetut itemit ja gemit.
4. **Stop [AURA]** palauttaa kaiken (taas yksi UAC-kysely). Sama tapahtuu automaattisesti, kun suljet GrowTool77:n.

Jos ohjelma kaatuu tai lupaa ei anneta, Main-välilehdelle ilmestyy **Restore connection** -nappi,
joka poistaa [AURA]:n rivit hosts-tiedostosta. Ilman sitä Growtopia ei saa yhteyttä.

## Miksi kirjautuminen tarvitsee OpenSSL:n
Growtopian oma asiakas on rakennettu OpenSSL:n päälle, eikä se hyväksy Windowsin
TLS-toteutusta (Schannel). Siksi [AURA]:n paikallinen HTTPS-palvelin on tehty
samalla OpenSSL:llä ja samoilla kättelyasetuksilla kuin avoin GTProxy. Tämä
korjaa "Check your internet / VPN" -virheen, joka johtui juuri tästä.

## Mitä [AURA] tekee
- Lisää hosts-tiedostoon kaksi riviä merkintöjen `# [AURA] begin` / `# [AURA] end` väliin
  (www.growtopia1.com, www.growtopia2.com → 127.0.0.1). Muihin riveihin ei kosketa.
- Kuuntelee vain osoitteessa 127.0.0.1 (ei näy lähiverkkoon).
- Välittää pelin ja palvelimen paketit sellaisenaan. Ainoa muutos: palvelimen osoite
  (`server_data.php` ja `OnSendToServer`) ohjataan takaisin [AURA]:an.
- Lukee paketeista vain worldin nimen ja pudotetut itemit.

## Mitä [AURA] EI tee
- Ei lähetä mitään muualle kuin Growtopian omille palvelimille.
- Ei tallenna, lokita eikä lue salasanaa, tokeneita, tunnusta eikä chattia.
- Ei lue näppäimistöä, hiirtä, näyttöä, rekisteriä eikä pelin muistia.
- Ei kirjoita mihinkään tiedostoon paitsi hosts-tiedostoon. Loki on vain muistissa.
- Ei käytä mitään muuta palvelinta kuin sitä, jonka Windows antoi www.growtopia1.com / www.growtopia2.com -nimille
  ennen hosts-muutosta. Windows tarkistaa palvelimen varmenteen; jos se ei kelpaa Windowsille, [AURA] kertoo sen
  lokissa ja jatkaa samoin kuin Growtopia itse (peli ei tarkista varmenteita).

## Tarkistukset käynnistyksessä
Ennen hosts-muutosta [AURA] testaa (1) vastaako oikea kirjautumispalvelin ja (2) toimiiko paikallinen
HTTPS tällä koneella. Jos (2) epäonnistuu, mitään ei muuteta ja syy näytetään.

## Testit
`AuraTests/` ajaa [AURA]:n täsmälleen samaa koodia tekopeliä ja tekopalvelimia vastaan
(oikea UDP- ja HTTPS-liikenne): `cd AuraTests && dotnet run -c Release`.

## Huomioitavaa
- Growtopian käyttöehdot kieltävät proxyt. Bannin riski on olemassa → testaa sivutilillä.
- Virustorjunta voi varoittaa hosts-tiedoston muutoksesta.
- Ongelmatilanteessa: **[AURA]**-välilehti → **Copy log** ja lähetä loki.
