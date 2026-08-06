# KOF Andrew Online

Código-fonte do servidor, launcher e atualizador do KOF Andrew Online.

## Distribuição

- Este repositório privado guarda somente código e ferramentas administrativas.
- Os jogadores recebem os binários pelo repositório público `KofAndrew-Updates`.
- O launcher consulta `latest.json`, verifica tamanho e SHA-256 de cada arquivo e só libera o servidor quando a instalação está íntegra.
- Atualizações são publicadas como GitHub Releases para não colocar o cliente de aproximadamente 1 GB no histórico Git.

## Pastas

- `KofOnlineLauncher`: launcher e lobby.
- `KofOnlineServer`: API da arena.
- `KofOnlineServerHost`: processo anfitrião do servidor.
- `KofOnlineUpdater`: aplicador externo de atualizações.
- `network`: configuração de firewall.
- `tools`: ferramentas de build e publicação.

Made By Andrew
