# How Yar is made

Yar is developed by its maintainer, [@mirinio](https://github.com/mirinio), with the help of
**Claude**, an AI assistant made by Anthropic, used through Claude Code.

## How the work is shared

The maintainer leads the project: decides what Yar does and how, writes and reviews code,
and tests the app on a real desktop. The assistant is used as a development tool: much of
the code, tests and documentation is written with it, it lays out options and trade-offs
before larger changes, and it runs reviews and the test container. Every change is directed
and accepted by the maintainer.

## How the work is checked

- **Automated tests**: around 470, run on every change and by CI. The code that unpacks
  archives, moves things to the Trash and refuses dangerous removals is tested against
  hostile cases, not only normal ones.
- **Real installs in throwaway containers**: every kind of install, removal, update and undo
  is carried out for real in Fedora and Ubuntu containers (`scripts/test-container.sh`),
  never on a computer someone uses.
- **Reviews**: the code that changes the system has been reviewed line by line, and those
  reviews found and fixed real bugs. More eyes are always welcome; reviewing that code is
  among the most valuable help you can give.
- **Honesty about gaps**: what has not been tried yet is written down in the README, the
  changelog and [SECURITY.md](SECURITY.md) rather than glossed over.

## Why say so

Because you are trusting this program with the software on your computer, you should know
how it is made. If you find something the checks above missed, please
[open an issue](https://github.com/mirinio/yar/issues), or report it privately if it is a
security problem.
