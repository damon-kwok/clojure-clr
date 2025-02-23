﻿;   Copyright (c) Rich Hickey. All rights reserved.
;   The use and distribution terms for this software are covered by the
;   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
;   which can be found in the file epl-v10.html at the root of this distribution.
;   By using this software in any fashion, you are agreeing to be bound by
;   the terms of this license.
;   You must not remove this notice, or any other, from this software.

; Author: Stuart Halloway

(ns clojure.test-clojure.rt
  (:require [clojure.string :as string]
            clojure.set)
  (:use clojure.test clojure.test-helper))

(defn bare-rt-print
  "Return string RT would print prior to print-initialize"
  [x]
  (with-out-str
    (try
     (push-thread-bindings {#'clojure.core/print-initialized false})
     (clojure.lang.RT/print x *out*)
     (finally
      (pop-thread-bindings)))))

(deftest rt-print-prior-to-print-initialize
  (testing "pattern literals"
    (is (= "#\"foo\"" (bare-rt-print #"foo")))))

(deftest error-messages
  (testing "binding a core var that already refers to something"
    (should-print-err-message
     #"WARNING: prefers already refers to: #'clojure.core/prefers in namespace: .*\r?\n"
     (defn prefers [] (throw (Exception. "rebound!")))))                                          ;;; RuntimeException
  (testing "reflection cannot resolve field"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - reference to field/property blah can't be resolved \(target class is unknown\).\r?\n"
     (defn foo [x] (.blah x))))
  (testing "reflection cannot resolve instance method on known class"              ;;; TODO: Figure out why the regexes don't match in these two tests.  They look identical to me.
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - reference to field/property blah on System\.String can't be resolved\.\r?\n"
     (defn foo [^String x] (.blah x))))
  (testing "reflection cannot resolve instance method because it is missing"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to method zap on System\.String can't be resolved \(no such method\)\.\r?\n"
     (defn foo [^String x] (.zap x 1))))
  (testing "reflection cannot resolve instance method because it has incompatible argument types"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to method IndexOf on System\.String can't be resolved \(argument types: System\.Double, clojure\.lang\.Symbol\)\.\r?\n"
     (defn foo [^String x] (.IndexOf x 12.1 'a))))
  (testing "reflection cannot resolve instance method because it has unknown argument types"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to method IndexOf on System\.String can't be resolved \(argument types: unknown\)\.\r?\n"
     (defn foo [^String x y] (.IndexOf x y))))
  (testing "reflection error prints correctly for nil arguments"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to method IndexOf on System.String can't be resolved \(argument types: unknown, unknown\)\.\r?\n"   ;;; divide on java\.math\.BigDecimal
     (defn foo [a] (.IndexOf "abc" a nil))))                                                       ;;; .(.Divide 1M a nil) -- we don't have an overload on this
  (testing "reflection cannot resolve instance method because target class is unknown"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to method zap can't be resolved \(target class is unknown\)\.\r?\n"
     (defn foo [x] (.zap x 1))))
  (testing "reflection cannot resolve static method"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to static method Format on System\.String can't be resolved \(argument types: System\.Text\.RegularExpressions\.Regex, System\.Int64\)\.\r?\n"
     (defn foo [] (String/Format #"boom" 12))))                                                            ;;; (defn foo [] (Integer/valueOf #"boom"))))
  (testing "reflection cannot resolved constructor"
    (should-print-err-message
     #"Reflection warning, .*:\d+:\d+ - call to System\.String ctor can't be resolved.\r?\n"       ;;; java.lang.String
     (defn foo [] (String. 1 2 3)))))

(def example-var)
(deftest binding-root-clears-macro-metadata
  (alter-meta! #'example-var assoc :macro true)
  (is (contains? (meta #'example-var) :macro))
  (.bindRoot #'example-var 0)
  (is (not (contains? (meta #'example-var) :macro))))

(deftest ns-intern-policies
  (testing "you can replace a core name, with warning"
    (let [ns (temp-ns)
          replacement (gensym)
          e1 (with-err-string-writer (intern ns 'prefers replacement))]
      (is (string/starts-with? e1 "WARNING"))
      (is (= replacement @('prefers (ns-publics ns))))))
  (testing "you can replace a defined alias"
    (let [ns (temp-ns)
          s (gensym)
          v1 (intern ns 'foo s)
          v2 (intern ns 'bar s)
          e1 (with-err-string-writer (.refer ns 'flatten v1))
          e2 (with-err-string-writer (.refer ns 'flatten v2))]
      (is (string/starts-with? e1 "WARNING"))
      (is (string/starts-with? e2 "WARNING"))
      (is (= v2 (ns-resolve ns 'flatten)))))
  (testing "you cannot replace an interned var"
    (let [ns1 (temp-ns)
          ns2 (temp-ns)
          v1 (intern ns1 'foo 1)
          v2 (intern ns2 'foo 2)
          e1 (with-err-string-writer (.refer ns1 'foo v2))]
      (is (string/starts-with? e1 "REJECTED"))
      (is (= v1 (ns-resolve ns1 'foo))))))
