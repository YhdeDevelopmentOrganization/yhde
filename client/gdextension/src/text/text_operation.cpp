#include "text/text_operation.h"

#include <godot_cpp/variant/variant.hpp>

#include <algorithm>

using namespace godot;

namespace yhde {

namespace {

// Walks the components of an edit, handing out pieces of them.
struct Cursor {
	const std::vector<TextOperation::Component> &ops;
	size_t index = 0;
	int64_t offset = 0; // consumed part of the current component

	explicit Cursor(const std::vector<TextOperation::Component> &o) : ops(o) {}
	bool done() const { return index >= ops.size(); }
	TextOperation::Kind kind() const { return ops[index].kind; }
	int64_t remaining() const { return ops[index].length() - offset; }
	String insert_piece(int64_t n) const { return ops[index].text.substr(offset, n); }
	void take(int64_t n) {
		offset += n;
		if (offset >= ops[index].length()) {
			index++;
			offset = 0;
		}
	}
};

} // namespace

TextOperation &TextOperation::retain(int64_t n) {
	if (n <= 0) return *this;
	base_ += n;
	target_ += n;
	if (!ops_.empty() && ops_.back().kind == Kind::Retain) {
		ops_.back().count += n;
	} else {
		ops_.push_back({ Kind::Retain, n, String() });
	}
	return *this;
}

TextOperation &TextOperation::insert(const String &s) {
	if (s.is_empty()) return *this;
	target_ += s.length();
	// Canonical form: an insert never directly follows a delete.
	if (!ops_.empty() && ops_.back().kind == Kind::Insert) {
		ops_.back().text += s;
	} else if (!ops_.empty() && ops_.back().kind == Kind::Delete) {
		if (ops_.size() > 1 && ops_[ops_.size() - 2].kind == Kind::Insert) {
			ops_[ops_.size() - 2].text += s;
		} else {
			ops_.insert(ops_.end() - 1, { Kind::Insert, 0, s });
		}
	} else {
		ops_.push_back({ Kind::Insert, 0, s });
	}
	return *this;
}

TextOperation &TextOperation::del(int64_t n) {
	if (n <= 0) return *this;
	base_ += n;
	if (!ops_.empty() && ops_.back().kind == Kind::Delete) {
		ops_.back().count += n;
	} else {
		ops_.push_back({ Kind::Delete, n, String() });
	}
	return *this;
}

bool TextOperation::is_noop() const {
	for (const Component &c : ops_) {
		if (c.kind != Kind::Retain) return false;
	}
	return true;
}

bool TextOperation::apply(const String &text, String &out) const {
	if (text.length() != base_) return false;
	String result;
	int64_t at = 0;
	for (const Component &c : ops_) {
		switch (c.kind) {
			case Kind::Retain:
				result += text.substr(at, c.count);
				at += c.count;
				break;
			case Kind::Insert:
				result += c.text;
				break;
			case Kind::Delete:
				at += c.count;
				break;
		}
	}
	result += text.substr(at);
	out = result;
	return true;
}

bool TextOperation::transform(const TextOperation &a, const TextOperation &b, TextOperation &a2, TextOperation &b2) {
	if (a.base_ != b.base_) return false;
	a2 = TextOperation();
	b2 = TextOperation();
	Cursor ia(a.ops_);
	Cursor ib(b.ops_);
	while (!ia.done() || !ib.done()) {
		if (!ia.done() && ia.kind() == Kind::Insert) {
			String s = ia.insert_piece(ia.remaining());
			a2.insert(s);
			b2.retain(s.length());
			ia.take(s.length());
			continue;
		}
		if (!ib.done() && ib.kind() == Kind::Insert) {
			String s = ib.insert_piece(ib.remaining());
			a2.retain(s.length());
			b2.insert(s);
			ib.take(s.length());
			continue;
		}
		if (ia.done() || ib.done()) return false;
		int64_t n = std::min(ia.remaining(), ib.remaining());
		Kind ka = ia.kind();
		Kind kb = ib.kind();
		if (ka == Kind::Retain && kb == Kind::Retain) {
			a2.retain(n);
			b2.retain(n);
		} else if (ka == Kind::Delete && kb == Kind::Retain) {
			a2.del(n);
		} else if (ka == Kind::Retain && kb == Kind::Delete) {
			b2.del(n);
		} // delete + delete: both removed the same characters
		ia.take(n);
		ib.take(n);
	}
	return true;
}

bool TextOperation::compose(const TextOperation &a, const TextOperation &b, TextOperation &out) {
	if (a.target_ != b.base_) return false;
	out = TextOperation();
	Cursor ia(a.ops_);
	Cursor ib(b.ops_);
	while (!ia.done() || !ib.done()) {
		if (!ia.done() && ia.kind() == Kind::Delete) {
			out.del(ia.remaining());
			ia.take(ia.remaining());
			continue;
		}
		if (!ib.done() && ib.kind() == Kind::Insert) {
			String s = ib.insert_piece(ib.remaining());
			out.insert(s);
			ib.take(s.length());
			continue;
		}
		if (ia.done() || ib.done()) return false;
		int64_t n = std::min(ia.remaining(), ib.remaining());
		Kind ka = ia.kind();
		Kind kb = ib.kind();
		if (ka == Kind::Retain && kb == Kind::Retain) {
			out.retain(n);
		} else if (ka == Kind::Insert && kb == Kind::Retain) {
			out.insert(ia.insert_piece(n));
		} else if (ka == Kind::Retain && kb == Kind::Delete) {
			out.del(n);
		} // insert + delete: the inserted text is removed again
		ia.take(n);
		ib.take(n);
	}
	return true;
}

TextOperation TextOperation::diff(const String &before, const String &after) {
	int64_t lb = before.length();
	int64_t la = after.length();
	int64_t prefix = 0;
	int64_t max_prefix = std::min(lb, la);
	const char32_t *pb = before.ptr();
	const char32_t *pa = after.ptr();
	while (prefix < max_prefix && pb[prefix] == pa[prefix]) prefix++;
	int64_t suffix = 0;
	while (suffix < max_prefix - prefix && pb[lb - 1 - suffix] == pa[la - 1 - suffix]) suffix++;
	TextOperation op;
	op.retain(prefix);
	op.del(lb - prefix - suffix);
	op.insert(after.substr(prefix, la - prefix - suffix));
	op.retain(suffix);
	return op;
}

TextOperation TextOperation::invert(const String &before) const {
	TextOperation inv;
	int64_t at = 0;
	for (const Component &c : ops_) {
		switch (c.kind) {
			case Kind::Retain:
				inv.retain(c.count);
				at += c.count;
				break;
			case Kind::Insert:
				inv.del(c.text.length());
				break;
			case Kind::Delete:
				inv.insert(before.substr(at, c.count));
				at += c.count;
				break;
		}
	}
	return inv;
}

bool TextOperation::from_array(const Array &a, TextOperation &out) {
	out = TextOperation();
	for (int64_t i = 0; i < a.size(); i++) {
		Variant v = a[i];
		switch (v.get_type()) {
			case Variant::INT:
			case Variant::FLOAT: {
				int64_t n = int64_t(double(v));
				if (n > 0) {
					out.retain(n);
				} else if (n < 0) {
					out.del(-n);
				} else {
					return false;
				}
				break;
			}
			case Variant::STRING: {
				String s = v;
				if (s.is_empty()) return false;
				out.insert(s);
				break;
			}
			default:
				return false;
		}
	}
	return true;
}

Array TextOperation::to_array() const {
	Array a;
	for (const Component &c : ops_) {
		switch (c.kind) {
			case Kind::Retain:
				a.push_back(c.count);
				break;
			case Kind::Insert:
				a.push_back(c.text);
				break;
			case Kind::Delete:
				a.push_back(-c.count);
				break;
		}
	}
	return a;
}

String normalize_text(const String &s) {
	String out = s;
	if (!out.is_empty() && out[0] == 0xFEFF) out = out.substr(1);
	return out.replace("\r\n", "\n");
}

} // namespace yhde
