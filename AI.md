# How Yar is made

Yar is written with **Claude**, an AI assistant made by Anthropic, working in Claude Code
under the direction of its maintainer, [@mirinio](https://github.com/mirinio).

## Who does what

- **The maintainer** decides what Yar should be and who it is for, makes every product and
  design decision, chooses between the approaches the assistant lays out, and uses the app.
- **The assistant** writes the code, the tests and the documentation, and explains its
  choices and their trade-offs before building anything larger.

## How the work is checked

- **Automated tests**: around 470, run on every change and by CI. The code that unpacks
  archives, moves things to the Trash and refuses dangerous removals is tested against
  hostile cases, not only normal ones.
- **Real installs in throwaway containers**: every kind of install, removal, update and undo
  is carried out for real in Fedora and Ubuntu containers (`scripts/test-container.sh`),
  never on a computer someone uses.
- **Reviews**: the code that changes the system was reviewed line by line in separate
  review passes by the assistant, which found and fixed real bugs. None of this replaces
  review by people, which is the most valuable help you can give.
- **Honesty about gaps**: what has not been tried, such as the password prompt on a real
  desktop, is written down in the README, the changelog and [SECURITY.md](SECURITY.md)
  rather than glossed over.

## Why say so

Because you are trusting this program with the software on your computer, you should know
how it was made. If you find something the checks above missed, please
[open an issue](https://github.com/mirinio/yar/issues), or report it privately if it is a
security problem.
